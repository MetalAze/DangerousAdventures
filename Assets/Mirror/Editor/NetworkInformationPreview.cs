using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Mirror
{
    [CustomPreview(typeof(GameObject))]
    internal class NetworkInformationPreview : ObjectPreview
    {
        private class NetworkIdentityInfo
        {
            public GUIContent name;
            public GUIContent value;
        }

        private class NetworkBehaviourInfo
        {
            // This is here just so we can check if it's enabled/disabled
            public NetworkBehaviour behaviour;
            public GUIContent name;
        }

        private class Styles
        {
            public GUIStyle labelStyle = new GUIStyle(EditorStyles.label);
            public GUIStyle componentName = new GUIStyle(EditorStyles.boldLabel);
            public GUIStyle disabledName = new GUIStyle(EditorStyles.miniLabel);

            public Styles()
            {
                var fontColor = new Color(0.7f, 0.7f, 0.7f);
                labelStyle.padding.right += 20;
                labelStyle.normal.textColor = fontColor;
                labelStyle.active.textColor = fontColor;
                labelStyle.focused.textColor = fontColor;
                labelStyle.hover.textColor = fontColor;
                labelStyle.onNormal.textColor = fontColor;
                labelStyle.onActive.textColor = fontColor;
                labelStyle.onFocused.textColor = fontColor;
                labelStyle.onHover.textColor = fontColor;

                componentName.normal.textColor = fontColor;
                componentName.active.textColor = fontColor;
                componentName.focused.textColor = fontColor;
                componentName.hover.textColor = fontColor;
                componentName.onNormal.textColor = fontColor;
                componentName.onActive.textColor = fontColor;
                componentName.onFocused.textColor = fontColor;
                componentName.onHover.textColor = fontColor;

                disabledName.normal.textColor = fontColor;
                disabledName.active.textColor = fontColor;
                disabledName.focused.textColor = fontColor;
                disabledName.hover.textColor = fontColor;
                disabledName.onNormal.textColor = fontColor;
                disabledName.onActive.textColor = fontColor;
                disabledName.onFocused.textColor = fontColor;
                disabledName.onHover.textColor = fontColor;
            }
        }

        private List<NetworkIdentityInfo> info;
        private List<NetworkBehaviourInfo> behavioursInfo;
        private NetworkIdentity identity;
        private GUIContent title;
        private Styles styles = new Styles();

        public override void Initialize(UnityObject[] targets)
        {
            base.Initialize(targets);
            GetNetworkInformation(target as GameObject);
        }

        public override GUIContent GetPreviewTitle()
        {
            if (title == null) title = new GUIContent("Network Information");
            return title;
        }

        public override bool HasPreviewGUI()
        {
            return info != null && info.Count > 0;
        }

        public override void OnPreviewGUI(Rect r, GUIStyle background)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            // refresh the data
            GetNetworkInformation(target as GameObject);

            if (info == null || info.Count == 0)
                return;

            if (styles == null)
                styles = new Styles();

            // Get required label size for the names of the information values we're going to show
            // There are two columns, one with label for the name of the info and the next for the value
            var maxNameLabelSize = new Vector2(140, 16);
            var maxValueLabelSize = GetMaxNameLabelSize();

            //Apply padding
            var previewPadding = new RectOffset(-5, -5, -5, -5);
            var paddedr = previewPadding.Add(r);

            //Centering
            var initialX = paddedr.x + 10;
            var initialY = paddedr.y + 10;

            var labelRect = new Rect(initialX, initialY, maxNameLabelSize.x, maxNameLabelSize.y);
            var idLabelRect = new Rect(maxNameLabelSize.x, initialY, maxValueLabelSize.x, maxValueLabelSize.y);

            foreach (var info in info)
            {
                GUI.Label(labelRect, info.name, styles.labelStyle);
                GUI.Label(idLabelRect, info.value, styles.componentName);
                labelRect.y += labelRect.height;
                labelRect.x = initialX;
                idLabelRect.y += idLabelRect.height;
            }

            // Show behaviours list in a different way than the name/value pairs above
            var lastY = labelRect.y;
            if (behavioursInfo != null && behavioursInfo.Count > 0)
            {
                var maxBehaviourLabelSize = GetMaxBehaviourLabelSize();
                var behaviourRect = new Rect(initialX, labelRect.y + 10, maxBehaviourLabelSize.x,
                    maxBehaviourLabelSize.y);

                GUI.Label(behaviourRect, new GUIContent("Network Behaviours"), styles.labelStyle);
                behaviourRect.x += 20; // indent names
                behaviourRect.y += behaviourRect.height;

                foreach (var info in behavioursInfo)
                {
                    if (info.behaviour == null)
                        // could be the case in the editor after existing play mode.
                        continue;

                    GUI.Label(behaviourRect, info.name,
                        info.behaviour.enabled ? styles.componentName : styles.disabledName);
                    behaviourRect.y += behaviourRect.height;
                    lastY = behaviourRect.y;
                }

                if (identity.observers != null && identity.observers.Count > 0)
                {
                    var observerRect = new Rect(initialX, lastY + 10, 200, 20);

                    GUI.Label(observerRect, new GUIContent("Network observers"), styles.labelStyle);
                    observerRect.x += 20; // indent names
                    observerRect.y += observerRect.height;

                    foreach (var kvp in identity.observers)
                    {
                        GUI.Label(observerRect, kvp.Value.address + ":" + kvp.Value, styles.componentName);
                        observerRect.y += observerRect.height;
                        lastY = observerRect.y;
                    }
                }

                if (identity.connectionToClient != null)
                {
                    var ownerRect = new Rect(initialX, lastY + 10, 400, 20);
                    GUI.Label(ownerRect, new GUIContent("Client Authority: " + identity.connectionToClient),
                        styles.labelStyle);
                }
            }
        }

        // Get the maximum size used by the value of information items
        private Vector2 GetMaxNameLabelSize()
        {
            var maxLabelSize = Vector2.zero;
            foreach (var info in info)
            {
                var labelSize = styles.labelStyle.CalcSize(info.value);
                if (maxLabelSize.x < labelSize.x) maxLabelSize.x = labelSize.x;
                if (maxLabelSize.y < labelSize.y) maxLabelSize.y = labelSize.y;
            }

            return maxLabelSize;
        }

        private Vector2 GetMaxBehaviourLabelSize()
        {
            var maxLabelSize = Vector2.zero;
            foreach (var behaviour in behavioursInfo)
            {
                var labelSize = styles.labelStyle.CalcSize(behaviour.name);
                if (maxLabelSize.x < labelSize.x) maxLabelSize.x = labelSize.x;
                if (maxLabelSize.y < labelSize.y) maxLabelSize.y = labelSize.y;
            }

            return maxLabelSize;
        }

        private void GetNetworkInformation(GameObject gameObject)
        {
            identity = gameObject.GetComponent<NetworkIdentity>();
            if (identity != null)
            {
                info = new List<NetworkIdentityInfo>
                {
                    GetAssetId(),
                    GetString("Scene ID", identity.sceneId.ToString("X"))
                };

                if (!Application.isPlaying) return;

                info.Add(GetString("Network ID", identity.netId.ToString()));

                info.Add(GetBoolean("Is Client", identity.isClient));
                info.Add(GetBoolean("Is Server", identity.isServer));
                info.Add(GetBoolean("Has Authority", identity.hasAuthority));
                info.Add(GetBoolean("Is Local Player", identity.isLocalPlayer));

                var behaviours = gameObject.GetComponents<NetworkBehaviour>();
                if (behaviours.Length > 0)
                {
                    behavioursInfo = new List<NetworkBehaviourInfo>();
                    foreach (var behaviour in behaviours)
                    {
                        var info = new NetworkBehaviourInfo
                        {
                            name = new GUIContent(behaviour.GetType().FullName),
                            behaviour = behaviour
                        };
                        behavioursInfo.Add(info);
                    }
                }
            }
        }

        private NetworkIdentityInfo GetAssetId()
        {
            var assetId = identity.assetId.ToString();
            if (string.IsNullOrEmpty(assetId)) assetId = "<object has no prefab>";
            return GetString("Asset ID", assetId);
        }

        private static NetworkIdentityInfo GetString(string name, string value)
        {
            var info = new NetworkIdentityInfo
            {
                name = new GUIContent(name),
                value = new GUIContent(value)
            };
            return info;
        }

        private static NetworkIdentityInfo GetBoolean(string name, bool value)
        {
            var info = new NetworkIdentityInfo
            {
                name = new GUIContent(name),
                value = new GUIContent(value ? "Yes" : "No")
            };
            return info;
        }
    }
}