using UnityEngine;
using UnityEngine.Serialization;

namespace Mirror
{
    /// <summary>
    /// A component to synchronize Mecanim animation states for networked objects.
    /// </summary>
    /// <remarks>
    /// <para>The animation of game objects can be networked by this component. There are two models of authority for networked movement:</para>
    /// <para>If the object has authority on the client, then it should animated locally on the owning client. The animation state information will be sent from the owning client to the server, then broadcast to all of the other clients. This is common for player objects.</para>
    /// <para>If the object has authority on the server, then it should be animated on the server and state information will be sent to all clients. This is common for objects not related to a specific client, such as an enemy unit.</para>
    /// <para>The NetworkAnimator synchronizes the animation parameters that are checked in the inspector view. It does not automatically sychronize triggers. The function SetTrigger can by used by an object with authority to fire an animation trigger on other clients.</para>
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("Network/NetworkAnimator")]
    [RequireComponent(typeof(NetworkIdentity))]
    [HelpURL("https://mirror-networking.com/docs/Components/NetworkAnimator.html")]
    public class NetworkAnimator : NetworkBehaviour
    {
        [Header("Authority")]
        [Tooltip(
            "Set to true if animations come from owner client,  set to false if animations always come from server")]
        public bool clientAuthority;

        /// <summary>
        /// The animator component to synchronize.
        /// </summary>
        [FormerlySerializedAs("m_Animator")]
        [Header("Animator")]
        [Tooltip("Animator that will have parameters synchronized")]
        public Animator animator;

        // Note: not an object[] array because otherwise initialization is real annoying
        private int[] lastIntParameters;
        private float[] lastFloatParameters;
        private bool[] lastBoolParameters;
        private AnimatorControllerParameter[] parameters;

        private int[] animationHash; // multiple layers
        private int[] transitionHash;
        private float sendTimer;

        private bool sendMessagesAllowed
        {
            get
            {
                if (isServer)
                {
                    if (!clientAuthority)
                        return true;

                    // This is a special case where we have client authority but we have not assigned the client who has
                    // authority over it, no animator data will be sent over the network by the server.
                    //
                    // So we check here for a connectionToClient and if it is null we will
                    // let the server send animation data until we receive an owner.
                    if (netIdentity != null && netIdentity.connectionToClient == null)
                        return true;
                }

                return hasAuthority && clientAuthority;
            }
        }

        private void Awake()
        {
            // store the animator parameters in a variable - the "Animator.parameters" getter allocates
            // a new parameter array every time it is accessed so we should avoid doing it in a loop
            parameters = animator.parameters;
            lastIntParameters = new int[parameters.Length];
            lastFloatParameters = new float[parameters.Length];
            lastBoolParameters = new bool[parameters.Length];

            animationHash = new int[animator.layerCount];
            transitionHash = new int[animator.layerCount];
        }

        private void FixedUpdate()
        {
            if (!sendMessagesAllowed)
                return;

            CheckSendRate();

            for (var i = 0; i < animator.layerCount; i++)
            {
                int stateHash;
                float normalizedTime;
                if (!CheckAnimStateChanged(out stateHash, out normalizedTime, i)) continue;

                var writer = NetworkWriterPool.GetWriter();
                WriteParameters(writer);

                SendAnimationMessage(stateHash, normalizedTime, i, writer.ToArray());
                NetworkWriterPool.Recycle(writer);
            }
        }

        private bool CheckAnimStateChanged(out int stateHash, out float normalizedTime, int layerId)
        {
            stateHash = 0;
            normalizedTime = 0;

            if (animator.IsInTransition(layerId))
            {
                var tt = animator.GetAnimatorTransitionInfo(layerId);
                if (tt.fullPathHash != transitionHash[layerId])
                {
                    // first time in this transition
                    transitionHash[layerId] = tt.fullPathHash;
                    animationHash[layerId] = 0;
                    return true;
                }

                return false;
            }

            var st = animator.GetCurrentAnimatorStateInfo(layerId);
            if (st.fullPathHash != animationHash[layerId])
            {
                // first time in this animation state
                if (animationHash[layerId] != 0)
                {
                    // came from another animation directly - from Play()
                    stateHash = st.fullPathHash;
                    normalizedTime = st.normalizedTime;
                }

                transitionHash[layerId] = 0;
                animationHash[layerId] = st.fullPathHash;
                return true;
            }

            return false;
        }

        private void CheckSendRate()
        {
            if (sendMessagesAllowed && syncInterval > 0 && sendTimer < Time.time)
            {
                sendTimer = Time.time + syncInterval;

                var writer = NetworkWriterPool.GetWriter();
                if (WriteParameters(writer)) SendAnimationParametersMessage(writer.ToArray());
                NetworkWriterPool.Recycle(writer);
            }
        }

        private void SendAnimationMessage(int stateHash, float normalizedTime, int layerId, byte[] parameters)
        {
            if (isServer)
                RpcOnAnimationClientMessage(stateHash, normalizedTime, layerId, parameters);
            else if (ClientScene.readyConnection != null)
                CmdOnAnimationServerMessage(stateHash, normalizedTime, layerId, parameters);
        }

        private void SendAnimationParametersMessage(byte[] parameters)
        {
            if (isServer)
                RpcOnAnimationParametersClientMessage(parameters);
            else if (ClientScene.readyConnection != null) CmdOnAnimationParametersServerMessage(parameters);
        }

        private void HandleAnimMsg(int stateHash, float normalizedTime, int layerId, NetworkReader reader)
        {
            if (hasAuthority && clientAuthority)
                return;

            // usually transitions will be triggered by parameters, if not, play anims directly.
            // NOTE: this plays "animations", not transitions, so any transitions will be skipped.
            // NOTE: there is no API to play a transition(?)
            if (stateHash != 0) animator.Play(stateHash, layerId, normalizedTime);

            ReadParameters(reader);
        }

        private void HandleAnimParamsMsg(NetworkReader reader)
        {
            if (hasAuthority && clientAuthority)
                return;

            ReadParameters(reader);
        }

        private void HandleAnimTriggerMsg(int hash)
        {
            animator.SetTrigger(hash);
        }

        private void HandleAnimResetTriggerMsg(int hash)
        {
            animator.ResetTrigger(hash);
        }

        private ulong NextDirtyBits()
        {
            ulong dirtyBits = 0;
            for (var i = 0; i < parameters.Length; i++)
            {
                var par = parameters[i];
                var changed = false;
                if (par.type == AnimatorControllerParameterType.Int)
                {
                    var newIntValue = animator.GetInteger(par.nameHash);
                    changed = newIntValue != lastIntParameters[i];
                    if (changed) lastIntParameters[i] = newIntValue;
                }
                else if (par.type == AnimatorControllerParameterType.Float)
                {
                    var newFloatValue = animator.GetFloat(par.nameHash);
                    changed = Mathf.Abs(newFloatValue - lastFloatParameters[i]) > 0.001f;
                    if (changed) lastFloatParameters[i] = newFloatValue;
                }
                else if (par.type == AnimatorControllerParameterType.Bool)
                {
                    var newBoolValue = animator.GetBool(par.nameHash);
                    changed = newBoolValue != lastBoolParameters[i];
                    if (changed) lastBoolParameters[i] = newBoolValue;
                }

                if (changed) dirtyBits |= 1ul << i;
            }

            return dirtyBits;
        }

        private bool WriteParameters(NetworkWriter writer, bool forceAll = false)
        {
            var dirtyBits = forceAll ? ~0ul : NextDirtyBits();
            writer.WritePackedUInt64(dirtyBits);
            for (var i = 0; i < parameters.Length; i++)
            {
                if ((dirtyBits & (1ul << i)) == 0)
                    continue;

                var par = parameters[i];
                if (par.type == AnimatorControllerParameterType.Int)
                {
                    var newIntValue = animator.GetInteger(par.nameHash);
                    writer.WritePackedInt32(newIntValue);
                }
                else if (par.type == AnimatorControllerParameterType.Float)
                {
                    var newFloatValue = animator.GetFloat(par.nameHash);
                    writer.WriteSingle(newFloatValue);
                }
                else if (par.type == AnimatorControllerParameterType.Bool)
                {
                    var newBoolValue = animator.GetBool(par.nameHash);
                    writer.WriteBoolean(newBoolValue);
                }
            }

            return dirtyBits != 0;
        }

        private void ReadParameters(NetworkReader reader)
        {
            var dirtyBits = reader.ReadPackedUInt64();
            for (var i = 0; i < parameters.Length; i++)
            {
                if ((dirtyBits & (1ul << i)) == 0)
                    continue;

                var par = parameters[i];
                if (par.type == AnimatorControllerParameterType.Int)
                {
                    var newIntValue = reader.ReadPackedInt32();
                    animator.SetInteger(par.nameHash, newIntValue);
                }
                else if (par.type == AnimatorControllerParameterType.Float)
                {
                    var newFloatValue = reader.ReadSingle();
                    animator.SetFloat(par.nameHash, newFloatValue);
                }
                else if (par.type == AnimatorControllerParameterType.Bool)
                {
                    var newBoolValue = reader.ReadBoolean();
                    animator.SetBool(par.nameHash, newBoolValue);
                }
            }
        }

        /// <summary>
        /// Custom Serialization
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="initialState"></param>
        /// <returns></returns>
        public override bool OnSerialize(NetworkWriter writer, bool initialState)
        {
            if (initialState)
            {
                for (var i = 0; i < animator.layerCount; i++)
                    if (animator.IsInTransition(i))
                    {
                        var st = animator.GetNextAnimatorStateInfo(i);
                        writer.WriteInt32(st.fullPathHash);
                        writer.WriteSingle(st.normalizedTime);
                    }
                    else
                    {
                        var st = animator.GetCurrentAnimatorStateInfo(i);
                        writer.WriteInt32(st.fullPathHash);
                        writer.WriteSingle(st.normalizedTime);
                    }

                WriteParameters(writer, initialState);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Custom Deserialization
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="initialState"></param>
        public override void OnDeserialize(NetworkReader reader, bool initialState)
        {
            if (initialState)
            {
                for (var i = 0; i < animator.layerCount; i++)
                {
                    var stateHash = reader.ReadInt32();
                    var normalizedTime = reader.ReadSingle();
                    animator.Play(stateHash, i, normalizedTime);
                }

                ReadParameters(reader);
            }
        }

        /// <summary>
        /// Causes an animation trigger to be invoked for a networked object.
        /// <para>If local authority is set, and this is called from the client, then the trigger will be invoked on the server and all clients. If not, then this is called on the server, and the trigger will be called on all clients.</para>
        /// </summary>
        /// <param name="triggerName">Name of trigger.</param>
        public void SetTrigger(string triggerName)
        {
            SetTrigger(Animator.StringToHash(triggerName));
        }

        /// <summary>
        /// Causes an animation trigger to be reset for a networked object.
        /// <para>If local authority is set, and this is called from the client, then the trigger will be reset on the server and all clients. If not, then this is called on the server, and the trigger will be reset on all clients.</para>
        /// </summary>
        /// <param name="triggerName">Name of trigger.</param>
        public void ResetTrigger(string triggerName)
        {
            ResetTrigger(Animator.StringToHash(triggerName));
        }

        /// <summary>
        /// Causes an animation trigger to be invoked for a networked object.
        /// </summary>
        /// <param name="hash">Hash id of trigger (from the Animator).</param>
        public void SetTrigger(int hash)
        {
            if (clientAuthority)
            {
                if (!isClient)
                {
                    Debug.LogWarning("Tried to set animation in the server for a client-controlled animator");
                    return;
                }

                if (!hasAuthority)
                {
                    Debug.LogWarning("Only the client with authority can set animations");
                    return;
                }

                if (ClientScene.readyConnection != null)
                    CmdOnAnimationTriggerServerMessage(hash);
            }
            else
            {
                if (!isServer)
                {
                    Debug.LogWarning("Tried to set animation in the client for a server-controlled animator");
                    return;
                }

                RpcOnAnimationTriggerClientMessage(hash);
            }
        }

        /// <summary>
        /// Causes an animation trigger to be reset for a networked object.
        /// </summary>
        /// <param name="hash">Hash id of trigger (from the Animator).</param>
        public void ResetTrigger(int hash)
        {
            if (clientAuthority)
            {
                if (!isClient)
                {
                    Debug.LogWarning("Tried to reset animation in the server for a client-controlled animator");
                    return;
                }

                if (!hasAuthority)
                {
                    Debug.LogWarning("Only the client with authority can reset animations");
                    return;
                }

                if (ClientScene.readyConnection != null)
                    CmdOnAnimationResetTriggerServerMessage(hash);
            }
            else
            {
                if (!isServer)
                {
                    Debug.LogWarning("Tried to reset animation in the client for a server-controlled animator");
                    return;
                }

                RpcOnAnimationResetTriggerClientMessage(hash);
            }
        }

        #region server message handlers

        [Command]
        private void CmdOnAnimationServerMessage(int stateHash, float normalizedTime, int layerId, byte[] parameters)
        {
            if (LogFilter.Debug) Debug.Log("OnAnimationMessage for netId=" + netId);

            // handle and broadcast
            var networkReader = NetworkReaderPool.GetReader(parameters);
            HandleAnimMsg(stateHash, normalizedTime, layerId, networkReader);
            NetworkReaderPool.Recycle(networkReader);

            RpcOnAnimationClientMessage(stateHash, normalizedTime, layerId, parameters);
        }

        [Command]
        private void CmdOnAnimationParametersServerMessage(byte[] parameters)
        {
            // handle and broadcast
            var networkReader = NetworkReaderPool.GetReader(parameters);
            HandleAnimParamsMsg(networkReader);
            NetworkReaderPool.Recycle(networkReader);

            RpcOnAnimationParametersClientMessage(parameters);
        }

        [Command]
        private void CmdOnAnimationTriggerServerMessage(int hash)
        {
            // handle and broadcast
            HandleAnimTriggerMsg(hash);
            RpcOnAnimationTriggerClientMessage(hash);
        }

        [Command]
        private void CmdOnAnimationResetTriggerServerMessage(int hash)
        {
            // handle and broadcast
            HandleAnimResetTriggerMsg(hash);
            RpcOnAnimationResetTriggerClientMessage(hash);
        }

        #endregion

        #region client message handlers

        [ClientRpc]
        private void RpcOnAnimationClientMessage(int stateHash, float normalizedTime, int layerId, byte[] parameters)
        {
            var networkReader = NetworkReaderPool.GetReader(parameters);
            HandleAnimMsg(stateHash, normalizedTime, layerId, networkReader);
            NetworkReaderPool.Recycle(networkReader);
        }

        [ClientRpc]
        private void RpcOnAnimationParametersClientMessage(byte[] parameters)
        {
            var networkReader = NetworkReaderPool.GetReader(parameters);
            HandleAnimParamsMsg(networkReader);
            NetworkReaderPool.Recycle(networkReader);
        }

        [ClientRpc]
        private void RpcOnAnimationTriggerClientMessage(int hash)
        {
            HandleAnimTriggerMsg(hash);
        }

        [ClientRpc]
        private void RpcOnAnimationResetTriggerClientMessage(int hash)
        {
            HandleAnimResetTriggerMsg(hash);
        }

        #endregion
    }
}