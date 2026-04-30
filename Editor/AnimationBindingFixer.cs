// 茶清墨刂 & DeepSeek

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine.SceneManagement;

public class AnimationBindingFixer : EditorWindow
{
    private enum ScanSource { Selection, SpecifiedRoot, EntireScene }
    private ScanSource scanSource = ScanSource.SpecifiedRoot;

    private string[] scanSourceDisplayNames = new string[] { "选择对象", "指定根对象", "整个场景" };

    private GameObject specifiedRoot;
    private bool scanAllProject = true;
    private string animationFolder = "";

    private List<GameObject> detectedAvatars = new List<GameObject>();
    private bool showAvatarList = true;

    // 实时自动扫描（仅扫描，不会自动修复）
    private bool autoScanEnabled = false;
    private double lastHierarchyChangeTime = 0;
    private bool pendingAutoScan = false;
    private const double AUTO_SCAN_DELAY = 0.5;

    // 批量修复
    private bool showBatchFix = true;
    private Dictionary<string, GameObject> batchTargets = new Dictionary<string, GameObject>();

    private List<BindingIssue> issues = new List<BindingIssue>();
    private Vector2 scrollPos;

    private class CandidateList
    {
        public GameObject[] exact;
        public GameObject[] fuzzy;
    }

    private class BindingIssue
    {
        public GameObject rootObject;
        public AnimationClip clip;
        public string missingPath;
        public string nodeName;
        public CandidateList candidates;
        public GameObject manualTarget;
        public bool isFixed;
    }

    [MenuItem("Tools/动画绑定修复器")]
    public static void ShowWindow() => GetWindow<AnimationBindingFixer>("动画绑定修复器 @茶清墨刂");

    private void OnEnable()
    {
        LoadSettings();
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        SaveSettings();
        EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        EditorApplication.update -= OnEditorUpdate;
    }

    private void OnHierarchyChanged()
    {
        lastHierarchyChangeTime = EditorApplication.timeSinceStartup;
        pendingAutoScan = true;
    }

    private void OnEditorUpdate()
    {
        if (!autoScanEnabled || !pendingAutoScan) return;

        if (EditorApplication.timeSinceStartup - lastHierarchyChangeTime >= AUTO_SCAN_DELAY)
        {
            pendingAutoScan = false;
            PerformAutoScan();
        }
    }

    private void PerformAutoScan()
    {
        GameObject[] roots = GetScanRoots();
        if (roots == null || roots.Length == 0)
        {
            Debug.LogWarning("[动画绑定修复器] 实时扫描：未找到有效的扫描根对象。");
            return;
        }

        if (!scanAllProject && string.IsNullOrEmpty(animationFolder))
        {
            Debug.LogWarning("[动画绑定修复器] 实时扫描：未指定动画来源。");
            return;
        }

        List<BindingIssue> freshIssues;
        ScanAndFixCore(roots, silent: true, out freshIssues);

        // 根据最新场景状态更新已知问题
        foreach (var issue in issues)
        {
            if (issue.rootObject == null) continue;
            bool pathExists = issue.rootObject.transform.Find(issue.missingPath) != null;
            if (issue.isFixed && !pathExists)
            {
                issue.isFixed = false;
                Debug.Log($"[动画绑定修复器] 路径再次缺失: {issue.clip.name} [{issue.missingPath}]");
            }
            else if (!issue.isFixed && pathExists)
            {
                issue.isFixed = true;
                Debug.Log($"[动画绑定修复器] 路径已恢复: {issue.clip.name} [{issue.missingPath}]");
            }
        }

        // 合并新发现的缺失
        foreach (var freshIssue in freshIssues)
        {
            if (!issues.Any(i => i.missingPath == freshIssue.missingPath && i.clip == freshIssue.clip && i.rootObject == freshIssue.rootObject))
                issues.Add(freshIssue);
        }

        int remaining = issues.Count(i => !i.isFixed);
        if (remaining > 0)
            Debug.LogWarning($"[动画绑定修复器] 实时扫描：当前 {remaining} 个缺失路径，请打开工具窗口查看。");
        else
            Debug.Log("[动画绑定修复器] 实时扫描：未发现缺失路径。");

        Repaint();
    }

    private void ManualScan()
    {
        if (specifiedRoot == null) specifiedRoot = DetectAvatarRoot();
        GameObject[] roots = GetScanRoots();
        if (roots == null || roots.Length == 0)
        {
            EditorUtility.DisplayDialog("提示", "未找到有效的扫描目标。", "确定");
            return;
        }
        if (!scanAllProject && string.IsNullOrEmpty(animationFolder))
        {
            EditorUtility.DisplayDialog("提示", "请指定动画来源", "确定");
            return;
        }
        issues.Clear();
        ScanAndFixCore(roots, silent: false, out issues);
        Repaint();
    }

    private void ScanAllRoles()
    {
        var roots = detectedAvatars.ToArray();
        if (roots.Length == 0)
        {
            EditorUtility.DisplayDialog("提示", "没有可用的角色。", "确定");
            return;
        }
        issues.Clear();
        ScanAndFixCore(roots, silent: false, out issues);
        Repaint();
    }

    private void ScanAndFixCore(GameObject[] roots, bool silent, out List<BindingIssue> outIssues)
    {
        List<AnimationClip> clipsToCheck = new List<AnimationClip>();

        foreach (var root in roots)
        {
            var animators = root.GetComponentsInChildren<Animator>(true);
            foreach (var animator in animators)
                if (animator.runtimeAnimatorController != null)
                    clipsToCheck.AddRange(animator.runtimeAnimatorController.animationClips);

            var animations = root.GetComponentsInChildren<Animation>(true);
            foreach (var anim in animations)
                foreach (AnimationState state in anim)
                    if (state.clip != null)
                        clipsToCheck.Add(state.clip);
        }

        if (scanAllProject)
        {
            string[] guids = AssetDatabase.FindAssets("t:AnimationClip");
            if (!silent && guids.Length > 100)
            {
                if (!EditorUtility.DisplayDialog("大量动画", $"项目中共有 {guids.Length} 个动画文件，继续？", "继续", "取消"))
                {
                    outIssues = new List<BindingIssue>();
                    return;
                }
            }
            if (!silent) EditorUtility.DisplayProgressBar("动画绑定修复器", "加载所有动画...", 0);
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip != null) clipsToCheck.Add(clip);
                if (!silent && i % 50 == 0)
                    EditorUtility.DisplayProgressBar("动画绑定修复器", $"加载动画... ({i}/{guids.Length})", (float)i / guids.Length);
            }
            if (!silent) EditorUtility.ClearProgressBar();
        }
        else if (!string.IsNullOrEmpty(animationFolder))
        {
            string fullPath = Path.Combine(Application.dataPath, animationFolder.Substring("Assets/".Length));
            if (Directory.Exists(fullPath))
            {
                string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { animationFolder });
                if (!silent) EditorUtility.DisplayProgressBar("动画绑定修复器", "加载文件夹动画...", 0);
                for (int i = 0; i < guids.Length; i++)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                    AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                    if (clip != null) clipsToCheck.Add(clip);
                    if (!silent && i % 20 == 0)
                        EditorUtility.DisplayProgressBar("动画绑定修复器", $"加载动画... ({i}/{guids.Length})", (float)i / guids.Length);
                }
                if (!silent) EditorUtility.ClearProgressBar();
                Debug.Log($"从文件夹加载了 {guids.Length} 个动画文件。");
            }
            else
            {
                if (!silent)
                {
                    bool useAll = EditorUtility.DisplayDialog("文件夹不存在", $"路径 {animationFolder} 无效。\n改为全项目扫描？", "是", "否");
                    if (useAll) { scanAllProject = true; ScanAndFixCore(roots, silent, out outIssues); return; }
                }
                outIssues = new List<BindingIssue>();
                return;
            }
        }
        else
        {
            if (!silent) EditorUtility.DisplayDialog("提示", "请指定动画来源", "确定");
            outIssues = new List<BindingIssue>();
            return;
        }

        clipsToCheck = clipsToCheck.Distinct().ToList();

        if (!silent) EditorUtility.DisplayProgressBar("动画绑定修复器", "扫描绑定...", 0);
        List<BindingIssue> tempIssues = new List<BindingIssue>();
        int total = clipsToCheck.Count * roots.Length;
        int count = 0;
        foreach (var root in roots)
        {
            foreach (var clip in clipsToCheck)
            {
                ScanClipAgainstRoot(root, clip, tempIssues);
                count++;
                if (!silent && count % 50 == 0)
                    EditorUtility.DisplayProgressBar("动画绑定修复器", $"扫描绑定... ({count}/{total})", (float)count / total);
            }
        }
        if (!silent) EditorUtility.ClearProgressBar();
        outIssues = tempIssues;
    }

    private void ScanClipAgainstRoot(GameObject root, AnimationClip clip, List<BindingIssue> issueList)
    {
        if (clip == null) return;
        var bindings = AnimationUtility.GetCurveBindings(clip);
        var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
        var allPaths = bindings.Select(b => b.path)
                        .Union(objectBindings.Select(b => b.path))
                        .Distinct();

        foreach (var path in allPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (root.transform.Find(path) == null)
            {
                string nodeName = path.Contains("/") ? path.Split('/').Last() : path;
                issueList.Add(new BindingIssue
                {
                    rootObject = root,
                    clip = clip,
                    missingPath = path,
                    nodeName = nodeName,
                    candidates = FindCandidates(nodeName, root.scene)
                });
            }
        }
    }

    private void FixPath(BindingIssue issue, GameObject newTarget)
    {
        if (issue.rootObject == null || issue.clip == null || newTarget == null) return;

        string oldPath = issue.missingPath;
        string newPath = GetRelativePath(issue.rootObject.transform, newTarget.transform);
        if (string.IsNullOrEmpty(newPath))
        {
            Debug.LogError($"无法计算相对路径：{newTarget.name} -> {issue.rootObject.name}");
            return;
        }

        Undo.RecordObject(issue.clip, "Fix Animation Path");

        var curveBindings = AnimationUtility.GetCurveBindings(issue.clip);
        foreach (var b in curveBindings)
        {
            if (b.path != oldPath) continue;
            var curve = AnimationUtility.GetEditorCurve(issue.clip, b);
            AnimationUtility.SetEditorCurve(issue.clip, b, null);
            var nb = new EditorCurveBinding { path = newPath, type = b.type, propertyName = b.propertyName };
            AnimationUtility.SetEditorCurve(issue.clip, nb, curve);
        }

        var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(issue.clip);
        foreach (var b in objBindings)
        {
            if (b.path != oldPath) continue;
            var curve = AnimationUtility.GetObjectReferenceCurve(issue.clip, b);
            AnimationUtility.SetObjectReferenceCurve(issue.clip, b, null);
            var nb = new EditorCurveBinding { path = newPath, type = b.type, propertyName = b.propertyName };
            AnimationUtility.SetObjectReferenceCurve(issue.clip, nb, curve);
        }

        EditorUtility.SetDirty(issue.clip);
        issue.isFixed = true;
        Debug.Log($"路径已修复: {issue.clip.name} [{oldPath} → {newPath}]");
        Repaint();
    }

    private void BatchFixByNodeName(string nodeName, GameObject target)
    {
        int count = 0;
        foreach (var issue in issues)
        {
            if (issue.isFixed) continue;
            if (issue.nodeName == nodeName)
            {
                FixPath(issue, target);
                count++;
            }
        }
        Debug.Log($"批量修复节点 '{nodeName}' 完成，修复了 {count} 个路径。");
        Repaint();
    }

    // 按场景层级结构排序（Hierarchy 窗口从上到下）
    private GameObject[] SortByHierarchy(GameObject[] objects, Scene scene)
    {
        if (objects.Length == 0) return objects;
        var rootObjects = scene.GetRootGameObjects();
        var orderDict = new Dictionary<GameObject, int>();
        int index = 0;
        void Traverse(Transform t)
        {
            orderDict[t.gameObject] = index++;
            for (int i = 0; i < t.childCount; i++)
                Traverse(t.GetChild(i));
        }
        foreach (var root in rootObjects)
            Traverse(root.transform);

        return objects.OrderBy(go => orderDict.TryGetValue(go, out int val) ? val : int.MaxValue).ToArray();
    }

    private CandidateList FindCandidates(string nodeName, Scene scene)
    {
        var allObjects = scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .Select(t => t.gameObject)
            .ToArray();

        var exact = allObjects.Where(go => go.name == nodeName).ToArray();
        var fuzzy = allObjects.Where(go =>
            go.name != nodeName && (
                go.name.IndexOf(nodeName, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                nodeName.IndexOf(go.name, System.StringComparison.OrdinalIgnoreCase) >= 0
            )
        ).Distinct().ToList();

        // 按场景层级结构排序
        exact = SortByHierarchy(exact, scene);
        fuzzy = SortByHierarchy(fuzzy.ToArray(), scene).ToList();

        return new CandidateList { exact = exact, fuzzy = fuzzy.ToArray() };
    }

    private string FindBestAnimationFolder(GameObject root)
    {
        string prefabFolder = GetPrefabSourceFolder(root);
        if (!string.IsNullOrEmpty(prefabFolder) && FolderContainsAnim(prefabFolder))
            return prefabFolder;
        if (!string.IsNullOrEmpty(prefabFolder))
        {
            string parentFolder = GetParentFolder(prefabFolder);
            if (parentFolder != null && FolderContainsAnim(parentFolder))
                return parentFolder;
        }
        string controllerFolder = GetControllerFolder(root);
        if (!string.IsNullOrEmpty(controllerFolder) && FolderContainsAnim(controllerFolder))
            return controllerFolder;
        if (!string.IsNullOrEmpty(controllerFolder))
        {
            string parentFolder = GetParentFolder(controllerFolder);
            if (parentFolder != null && FolderContainsAnim(parentFolder))
                return parentFolder;
        }
        return prefabFolder ?? "";
    }

    private string GetPrefabSourceFolder(GameObject obj)
    {
        if (PrefabUtility.GetPrefabInstanceStatus(obj) == PrefabInstanceStatus.Connected ||
            PrefabUtility.GetPrefabAssetType(obj) != PrefabAssetType.NotAPrefab)
        {
            Object source = PrefabUtility.GetCorrespondingObjectFromSource(obj);
            string path = AssetDatabase.GetAssetPath(source);
            if (!string.IsNullOrEmpty(path))
                return Path.GetDirectoryName(path).Replace("\\", "/");
        }
        return null;
    }

    private string GetControllerFolder(GameObject root)
    {
        var animator = root.GetComponentInChildren<Animator>(true);
        if (animator != null && animator.runtimeAnimatorController != null)
        {
            string path = AssetDatabase.GetAssetPath(animator.runtimeAnimatorController);
            if (!string.IsNullOrEmpty(path))
                return Path.GetDirectoryName(path).Replace("\\", "/");
        }
        return null;
    }

    private bool FolderContainsAnim(string folderPath)
    {
        string fullPath = Path.Combine(Application.dataPath, folderPath.Substring("Assets/".Length));
        return Directory.Exists(fullPath) && Directory.GetFiles(fullPath, "*.anim", SearchOption.TopDirectoryOnly).Length > 0;
    }

    private string GetParentFolder(string folder)
    {
        if (folder == "Assets") return null;
        return Path.GetDirectoryName(folder)?.Replace("\\", "/");
    }

    private List<GameObject> DetectAllAvatarRoots()
    {
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        var result = new List<GameObject>();
        foreach (var root in roots)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                if (go.GetComponents<Component>().Any(c =>
                    c.GetType().FullName == "VRC.SDKBase.VRC_AvatarDescriptor" ||
                    c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
                {
                    result.Add(go);
                    break;
                }
            }
        }
        if (result.Count == 0)
            result.AddRange(roots.Where(r => r.GetComponent<Animator>() != null));
        if (result.Count == 0)
            result.AddRange(roots);
        return result.Distinct().ToList();
    }

    private GameObject DetectAvatarRoot()
    {
        var all = DetectAllAvatarRoots();
        return all.FirstOrDefault();
    }

    private GameObject[] GetScanRoots()
    {
        switch (scanSource)
        {
            case ScanSource.SpecifiedRoot:
                return specifiedRoot != null ? new[] { specifiedRoot } : null;
            case ScanSource.Selection:
                return Selection.gameObjects.Length > 0 ? Selection.gameObjects : null;
            case ScanSource.EntireScene:
                return SceneManager.GetActiveScene().GetRootGameObjects();
            default: return null;
        }
    }

    private string GetRelativePath(Transform root, Transform target)
    {
        if (target == root) return "";
        string path = target.name;
        Transform parent = target.parent;
        while (parent != null && parent != root)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return parent == root ? path : null;
    }

    private string GetFullPath(GameObject obj)
    {
        string path = obj.name;
        Transform parent = obj.transform.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }

    private void SaveSettings()
    {
        EditorPrefs.SetInt("AnimFixer_ScanSource", (int)scanSource);
        EditorPrefs.SetBool("AnimFixer_ScanAllProject", scanAllProject);
        EditorPrefs.SetString("AnimFixer_AnimationFolder", animationFolder);
        EditorPrefs.SetBool("AnimFixer_ShowAvatarList", showAvatarList);
        EditorPrefs.SetBool("AnimFixer_AutoScanEnabled", autoScanEnabled);
        EditorPrefs.SetBool("AnimFixer_ShowBatchFix", showBatchFix);
        if (specifiedRoot != null) EditorPrefs.SetString("AnimFixer_SpecifiedRootName", specifiedRoot.name);
        else EditorPrefs.SetString("AnimFixer_SpecifiedRootName", "");
    }

    private void LoadSettings()
    {
        scanSource = (ScanSource)EditorPrefs.GetInt("AnimFixer_ScanSource", (int)ScanSource.SpecifiedRoot);
        scanAllProject = EditorPrefs.GetBool("AnimFixer_ScanAllProject", true);
        animationFolder = EditorPrefs.GetString("AnimFixer_AnimationFolder", "");
        showAvatarList = EditorPrefs.GetBool("AnimFixer_ShowAvatarList", true);
        autoScanEnabled = EditorPrefs.GetBool("AnimFixer_AutoScanEnabled", false);
        showBatchFix = EditorPrefs.GetBool("AnimFixer_ShowBatchFix", true);

        string savedRootName = EditorPrefs.GetString("AnimFixer_SpecifiedRootName", "");
        if (!string.IsNullOrEmpty(savedRootName)) specifiedRoot = FindSceneObjectByName(savedRootName);
    }

    private GameObject FindSceneObjectByName(string name)
    {
        return SceneManager.GetActiveScene().GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .Select(t => t.gameObject)
            .FirstOrDefault(go => go.name == name);
    }

    // ========== UI ==========
    private void OnGUI()
    {
        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("扫描来源", EditorStyles.boldLabel);
        // 使用中文下拉选择
        scanSource = (ScanSource)EditorGUILayout.Popup("来源", (int)scanSource, scanSourceDisplayNames);

        if (scanSource == ScanSource.SpecifiedRoot)
        {
            EditorGUILayout.BeginHorizontal();
            specifiedRoot = (GameObject)EditorGUILayout.ObjectField("根对象", specifiedRoot, typeof(GameObject), true);
            if (GUILayout.Button("自动检测角色", GUILayout.Width(100)))
            {
                specifiedRoot = DetectAvatarRoot();
                if (specifiedRoot != null)
                {
                    Debug.Log($"已自动设置角色根对象: {specifiedRoot.name}");
                    animationFolder = FindBestAnimationFolder(specifiedRoot);
                }
                else
                    EditorUtility.DisplayDialog("未找到角色", "场景中未检测到 VRC Avatar Descriptor 或 Animator 组件。", "确定");
            }
            EditorGUILayout.EndHorizontal();

            showAvatarList = EditorGUILayout.Foldout(showAvatarList, $"场景中的角色 ({detectedAvatars.Count})", true);
            if (showAvatarList)
            {
                if (GUILayout.Button("刷新角色列表", GUILayout.Height(20)))
                    detectedAvatars = DetectAllAvatarRoots();

                if (detectedAvatars.Count > 0)
                {
                    EditorGUILayout.BeginVertical("box");
                    foreach (var avatar in detectedAvatars)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(avatar.name, GetFullPath(avatar), EditorStyles.miniLabel);
                        if (GUILayout.Button("设为根", GUILayout.Width(60)))
                        {
                            specifiedRoot = avatar;
                            animationFolder = FindBestAnimationFolder(avatar);
                            Debug.Log($"已切换根对象为: {avatar.name}");
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndVertical();

                    if (GUILayout.Button("扫描所有角色", GUILayout.Height(25)))
                        ScanAllRoles();
                }
                else
                    EditorGUILayout.HelpBox("点击「刷新角色列表」检测场景中的角色。", MessageType.Info);
            }
        }
        else if (scanSource == ScanSource.Selection)
            EditorGUILayout.HelpBox("请先在 Hierarchy 中选中一个或多个物体。", MessageType.Info);

        EditorGUILayout.Space(5);

        EditorGUILayout.LabelField("动画来源", EditorStyles.boldLabel);
        bool newScanAll = EditorGUILayout.ToggleLeft("扫描整个项目中的所有动画文件", scanAllProject);
        if (newScanAll != scanAllProject)
        {
            scanAllProject = newScanAll;
            if (scanAllProject) Debug.Log("已启用全项目动画扫描。");
        }

        if (!scanAllProject)
        {
            EditorGUILayout.BeginHorizontal();
            animationFolder = EditorGUILayout.TextField("动画文件夹 (Assets/...)", animationFolder);
            if (GUILayout.Button("选择", GUILayout.Width(50)))
            {
                string sel = EditorUtility.OpenFolderPanel("选择动画文件夹", "Assets", "");
                if (!string.IsNullOrEmpty(sel) && sel.StartsWith(Application.dataPath))
                    animationFolder = "Assets" + sel.Substring(Application.dataPath.Length);
                else if (!string.IsNullOrEmpty(sel))
                    EditorUtility.DisplayDialog("错误", "请选择 Assets 目录下的文件夹。", "确定");
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("将扫描此文件夹及其子文件夹中的 .anim 文件。", MessageType.None);
        }
        else
            EditorGUILayout.HelpBox("全项目扫描所有 .anim 文件，文件较多时可能较慢。", MessageType.Warning);

        // 实时自动扫描开关
        EditorGUILayout.Space(5);
        bool newAutoScan = EditorGUILayout.ToggleLeft("🔄 实时自动扫描（仅收集缺失，不会自动修复）", autoScanEnabled);
        if (newAutoScan != autoScanEnabled)
        {
            if (newAutoScan && !scanAllProject && string.IsNullOrEmpty(animationFolder))
            {
                EditorUtility.DisplayDialog("无法启用", "请先指定动画来源。", "确定");
                newAutoScan = false;
            }
            else
            {
                autoScanEnabled = newAutoScan;
                Debug.Log(autoScanEnabled ? "实时自动扫描已开启。" : "实时自动扫描已关闭。");
            }
        }
        if (autoScanEnabled)
            EditorGUILayout.HelpBox("实时模式已开启，物体变化后自动收集缺失路径。", MessageType.Info);

        EditorGUILayout.Space(5);
        if (GUILayout.Button("扫描", GUILayout.Height(30)))
            ManualScan();

        // 批量修复面板
        if (issues.Count > 0)
        {
            EditorGUILayout.Space(10);
            showBatchFix = EditorGUILayout.Foldout(showBatchFix, "批量修复（相同缺失节点名）", true);
            if (showBatchFix)
            {
                var nodeGroups = issues.Where(i => !i.isFixed)
                    .GroupBy(i => i.nodeName)
                    .Select(g => new { nodeName = g.Key, count = g.Count() })
                    .OrderByDescending(g => g.count)
                    .ToList();

                if (nodeGroups.Count == 0)
                    EditorGUILayout.HelpBox("没有未修复的缺失路径。", MessageType.Info);
                else
                {
                    EditorGUILayout.BeginVertical("box");
                    foreach (var group in nodeGroups)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"{group.nodeName} (缺失 {group.count} 处)", GUILayout.Width(180));
                        if (!batchTargets.ContainsKey(group.nodeName))
                            batchTargets[group.nodeName] = null;
                        batchTargets[group.nodeName] = (GameObject)EditorGUILayout.ObjectField(batchTargets[group.nodeName], typeof(GameObject), true, GUILayout.Width(120));
                        if (GUILayout.Button("批量修复", GUILayout.Width(80)))
                        {
                            if (batchTargets[group.nodeName] != null)
                                BatchFixByNodeName(group.nodeName, batchTargets[group.nodeName]);
                            else
                                Debug.LogWarning($"请为节点 '{group.nodeName}' 指定目标物体。");
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndVertical();
                }
            }
        }

        // 问题列表
        int remaining = issues.Count(i => !i.isFixed);
        if (remaining > 0)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField($"剩余 {remaining} 个未修复的路径", EditorStyles.boldLabel);
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            foreach (var issue in issues)
            {
                if (issue.isFixed) continue;
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"[{issue.rootObject.name}] {issue.clip.name} : 路径 \"{issue.missingPath}\" 缺失", EditorStyles.wordWrappedLabel);
                if (GUILayout.Button("🔍", GUILayout.Width(25), GUILayout.Height(18)))
                {
                    Selection.activeObject = issue.clip;
                    EditorGUIUtility.PingObject(issue.clip);
                }
                EditorGUILayout.EndHorizontal();

                // 精确匹配（按层级排序）
                if (issue.candidates.exact.Length > 0)
                {
                    EditorGUILayout.LabelField("精确匹配对象：", EditorStyles.miniBoldLabel);
                    foreach (var candidate in issue.candidates.exact)
                        DrawCandidateRow(issue, candidate, false);
                }

                // 近似匹配（按层级排序）
                if (issue.candidates.fuzzy.Length > 0)
                {
                    EditorGUILayout.LabelField("近似匹配对象（名称包含关系）：", EditorStyles.miniBoldLabel);
                    foreach (var candidate in issue.candidates.fuzzy)
                        DrawCandidateRow(issue, candidate, true);
                }

                if (issue.candidates.exact.Length == 0 && issue.candidates.fuzzy.Length == 0)
                {
                    EditorGUILayout.LabelField("未找到任何匹配对象，请手动拖入：");
                }

                EditorGUILayout.BeginHorizontal();
                issue.manualTarget = (GameObject)EditorGUILayout.ObjectField("目标物体", issue.manualTarget, typeof(GameObject), true);
                if (issue.manualTarget != null && GUILayout.Button("强制修复", GUILayout.Width(80)))
                    FixPath(issue, issue.manualTarget);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }
        else if (issues.Count > 0 && remaining == 0)
            EditorGUILayout.HelpBox("所有缺失路径均已手动修复。", MessageType.Info);
        else if (issues.Count == 0 && autoScanEnabled)
            EditorGUILayout.HelpBox("当前没有缺失的动画绑定。", MessageType.Info);
    }

    private void DrawCandidateRow(BindingIssue issue, GameObject candidate, bool isFuzzy)
    {
        EditorGUILayout.BeginHorizontal();
        string label = candidate.name + " (" + GetFullPath(candidate) + ")";
        if (isFuzzy) label += " [近似]";
        EditorGUILayout.LabelField(label);

        if (GUILayout.Button("📍", GUILayout.Width(25), GUILayout.Height(18)))
        {
            Selection.activeGameObject = candidate;
            EditorGUIUtility.PingObject(candidate);
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        if (GUILayout.Button("使用", GUILayout.Width(50)))
            FixPath(issue, candidate);
        EditorGUILayout.EndHorizontal();
    }
}
