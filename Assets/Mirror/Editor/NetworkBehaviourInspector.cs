using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Mirror
{
    [CustomEditor(typeof(NetworkBehaviour), true)]
    [CanEditMultipleObjects]
    public class NetworkBehaviourInspector : Editor
    {
        private bool initialized;
        protected List<string> syncVarNames = new List<string>();
        private bool syncsAnything;
        private bool[] showSyncLists;

        private static readonly GUIContent syncVarIndicatorContent =
            new GUIContent("SyncVar", "This variable has been marked with the [SyncVar] attribute.");

        internal virtual bool HideScriptField => false;

        // does this type sync anything? otherwise we don't need to show syncInterval
        private bool SyncsAnything(Type scriptClass)
        {
            // has OnSerialize that is not in NetworkBehaviour?
            // then it either has a syncvar or custom OnSerialize. either way
            // this means we have something to sync.
            var method = scriptClass.GetMethod("OnSerialize");
            if (method != null && method.DeclaringType != typeof(NetworkBehaviour)) return true;

            // SyncObjects are serialized in NetworkBehaviour.OnSerialize, which
            // is always there even if we don't use SyncObjects. so we need to
            // search for SyncObjects manually.
            // (look for 'Mirror.Sync'. not '.SyncObject' because we'd have to
            //  check base type for that again)
            // => scan both public and non-public fields! SyncVars can be private
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var field in scriptClass.GetFields(flags))
                if (field.FieldType.BaseType != null &&
                    field.FieldType.BaseType.FullName != null &&
                    field.FieldType.BaseType.FullName.Contains("Mirror.Sync"))
                    return true;

            return false;
        }

        private void OnEnable()
        {
            initialized = false;
        }

        private void Init(MonoScript script)
        {
            initialized = true;
            var scriptClass = script.GetClass();

            // find public SyncVars to show (user doesn't want protected ones to be shown in inspector)
            foreach (var field in scriptClass.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var fieldMarkers = (Attribute[]) field.GetCustomAttributes(typeof(SyncVarAttribute), true);
                if (fieldMarkers.Length > 0) syncVarNames.Add(field.Name);
            }

            var numSyncLists = scriptClass.GetFields().Count(
                field => field.FieldType.BaseType != null &&
                         field.FieldType.BaseType.Name.Contains("SyncList"));
            if (numSyncLists > 0) showSyncLists = new bool[numSyncLists];

            syncsAnything = SyncsAnything(scriptClass);
        }

        public override void OnInspectorGUI()
        {
            if (!initialized)
            {
                serializedObject.Update();
                var scriptProperty = serializedObject.FindProperty("m_Script");
                if (scriptProperty == null)
                    return;

                var targetScript = scriptProperty.objectReferenceValue as MonoScript;
                Init(targetScript);
            }

            EditorGUI.BeginChangeCheck();
            serializedObject.Update();

            // Loop through properties and create one field (including children) for each top level property.
            var property = serializedObject.GetIterator();
            var expanded = true;
            while (property.NextVisible(expanded))
            {
                var isSyncVar = syncVarNames.Contains(property.name);

                if (property.name == "m_Script")
                {
                    if (HideScriptField) continue;

                    EditorGUI.BeginDisabledGroup(true);
                }

                if (isSyncVar)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.BeginVertical();
                }

                EditorGUILayout.PropertyField(property, true);

                if (isSyncVar)
                {
                    EditorGUILayout.EndVertical();
                    GUILayout.Label(syncVarIndicatorContent, EditorStyles.miniLabel,
                        GUILayout.Width(EditorStyles.miniLabel.CalcSize(syncVarIndicatorContent).x));
                    EditorGUILayout.EndHorizontal();
                }

                if (property.name == "m_Script") EditorGUI.EndDisabledGroup();

                expanded = false;
            }

            serializedObject.ApplyModifiedProperties();
            EditorGUI.EndChangeCheck();

            // find SyncLists.. they are not properties.
            var syncListIndex = 0;
            foreach (var field in serializedObject.targetObject.GetType().GetFields())
                if (field.FieldType.BaseType != null && field.FieldType.BaseType.Name.Contains("SyncList"))
                {
                    showSyncLists[syncListIndex] = EditorGUILayout.Foldout(showSyncLists[syncListIndex],
                        "SyncList " + field.Name + "  [" + field.FieldType.Name + "]");
                    if (showSyncLists[syncListIndex])
                    {
                        EditorGUI.indentLevel += 1;
                        if (field.GetValue(serializedObject.targetObject) is IEnumerable synclist)
                        {
                            var index = 0;
                            var enu = synclist.GetEnumerator();
                            while (enu.MoveNext())
                            {
                                if (enu.Current != null)
                                    EditorGUILayout.LabelField("Item:" + index, enu.Current.ToString());
                                index += 1;
                            }
                        }

                        EditorGUI.indentLevel -= 1;
                    }

                    syncListIndex += 1;
                }

            // does it sync anything? then show extra properties
            // (no need to show it if the class only has Cmds/Rpcs and no sync)
            if (syncsAnything)
            {
                var networkBehaviour = target as NetworkBehaviour;
                if (networkBehaviour != null)
                {
                    EditorGUILayout.LabelField("Sync Settings", EditorStyles.boldLabel);

                    // syncMode
                    serializedObject.FindProperty("syncMode").enumValueIndex = (int) (SyncMode)
                        EditorGUILayout.EnumPopup("Network Sync Mode", networkBehaviour.syncMode);

                    // syncInterval
                    // [0,2] should be enough. anything >2s is too laggy anyway.
                    serializedObject.FindProperty("syncInterval").floatValue = EditorGUILayout.Slider(
                        new GUIContent("Network Sync Interval",
                            "Time in seconds until next change is synchronized to the client. '0' means send immediately if changed. '0.5' means only send changes every 500ms.\n(This is for state synchronization like SyncVars, SyncLists, OnSerialize. Not for Cmds, Rpcs, etc.)"),
                        networkBehaviour.syncInterval, 0, 2);

                    // apply
                    serializedObject.ApplyModifiedProperties();
                }
            }
        }
    }
} //namespace