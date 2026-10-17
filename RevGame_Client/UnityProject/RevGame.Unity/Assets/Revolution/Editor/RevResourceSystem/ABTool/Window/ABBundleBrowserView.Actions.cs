// ============================================================
// ABBundleBrowserView.Actions.cs —— 「分包」页签的操作：新建 / 改名 / 删除 / 换层级 / 加入 / 移出 / 右键菜单
//
// 位置：Editor\RevResourceSystem\ABTool\Window\
//
// 【两条纪律】
//   ① 真正改标记只调 ABBundleEditor（界面不直接碰 AssetImporter）；
//   ② 会弹确认框的操作一律 ABGUI.Defer 到下一帧：在 OnGUI 中途弹模态框会打乱 GUILayout 配对。
//   改完调 Refresh()：立刻让共享缓存失效（不等自动同步的去抖），六个页签下一帧都是新数据。
// ============================================================
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    internal sealed partial class ABBundleBrowserView
    {
        private void Refresh(string reason)
        {
            _treeDirty = true;
            ABMarkerWatcher.RefreshNow(reason);
        }

        // ============================================================
        // 新建
        // ============================================================

        /// <summary>在 parent 这一层新建一个空包，并立刻进入改名（和 AssetBundle Browser 一样先给个默认名）</summary>
        internal void CreateBundle(string parent)
        {
            List<string> all = AllBundleNames();
            var existing = new HashSet<string>(all);
            foreach (string name in all)                              // 包"文件夹"的路径也不能重名
                for (string p = ABBundleEditor.ParentOf(name); p.Length > 0; p = ABBundleEditor.ParentOf(p)) existing.Add(p);

            string created = ABBundleEditor.MakeUnique(ABBundleEditor.Combine(parent, "newbundle"), existing);
            _pending.Add(created);

            _treeDirty = true;
            SyncData();
            _bundleTree.SelectPaths(new[] { created });
            _bundleTree.BeginRenamePath(created);
        }

        // ============================================================
        // 改名 / 换层级
        // ============================================================

        /// <summary>树里原地改名结束：oldPath（包或包文件夹）整体换成 newPath</summary>
        internal void RenameNode(string oldPath, string newPath)
        {
            if (!ABBundleEditor.IsLegalBundleName(newPath, out string why))
            {
                ABGUI.Defer(() => EditorUtility.DisplayDialog("包名不合法", $"「{newPath}」：{why}", "好"));
                return;
            }

            if (_bundleTree.Exists(newPath) && newPath != oldPath)
            {
                ABGUI.Defer(() => EditorUtility.DisplayDialog("重名", $"已经有「{newPath}」了，换一个名字。", "好"));
                return;
            }

            // 空包只存在于界面上：改一下名单就行
            RenamePending(oldPath, newPath);

            int changed = ABBundleEditor.RenamePrefix(oldPath, newPath);
            if (changed > 0) RevABLog.Info($"[RevAB] 「{oldPath}」已重命名为「{newPath}」（改了 {changed} 处标记）");

            Refresh("工具内重命名");
            ReselectAfterSync(newPath);
        }

        /// <summary>把若干个包（或包文件夹）挪进 newParent 这一层（拖放改层级）</summary>
        internal void MoveBundles(string[] paths, string newParent)
        {
            var moved = new List<string>();

            foreach (string path in paths)
            {
                string target = ABBundleEditor.Combine(newParent, LeafOf(path));
                if (target == path) continue;

                if (_bundleTree.Exists(target))
                {
                    string captured = target;
                    ABGUI.Defer(() => EditorUtility.DisplayDialog("重名", $"目标位置已经有「{captured}」了，这一项没挪。", "好"));
                    continue;
                }

                RenamePending(path, target);
                ABBundleEditor.RenamePrefix(path, target);
                moved.Add(target);
            }

            if (moved.Count == 0) return;

            RevABLog.Info($"[RevAB] 已调整 {moved.Count} 个包的层级 → 「{(newParent.Length == 0 ? "最外层" : newParent)}」");
            Refresh("工具内调整层级");
            ReselectAfterSync(moved.ToArray());
        }

        private void RenamePending(string oldPath, string newPath)
        {
            for (int i = 0; i < _pending.Count; i++)
                if (ABBundleEditor.IsUnder(_pending[i], oldPath))
                    _pending[i] = newPath + _pending[i].Substring(oldPath.Length);
        }

        private static string LeafOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        // ============================================================
        // 删除
        // ============================================================

        /// <summary>删除选中的包 / 包文件夹（先确认；只清标记，资源文件本身不动）</summary>
        internal void DeleteNodes(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return;

            ABGUI.Defer(() =>
            {
                string list = paths.Count <= 6 ? string.Join("\n", paths) : string.Join("\n", paths.GetRange(0, 6)) + "\n…";
                if (!EditorUtility.DisplayDialog("删除包",
                        $"要删除这 {paths.Count} 项吗？（选中文件夹 = 连同它下面的所有包）\n\n{list}\n\n" +
                        "会清空属于它们的 AB 标记，资源文件本身不会被删。\n" +
                        "给文件夹设的标记也会一起清掉，子文件随之退出这些包。",
                        "删除", "取消"))
                    return;

                int changed = 0;
                foreach (string path in paths)
                {
                    _pending.RemoveAll(name => ABBundleEditor.IsUnder(name, path));
                    changed += ABBundleEditor.DeletePrefix(path);
                }

                RevABLog.Info($"[RevAB] 已删除 {paths.Count} 项（清掉 {changed} 处标记）");
                _bundleState.selectedIDs.Clear();
                Refresh("工具内删除包");
            });
        }

        /// <summary>清理工程里"已经没有资源使用"的包名</summary>
        private void CleanupUnusedNames()
        {
            int removed = ABBundleEditor.RemoveUnusedNames();
            RevABLog.Info(removed > 0 ? $"[RevAB] 已清理 {removed} 个未使用的包名" : "[RevAB] 没有未使用的包名");
            Refresh("清理未使用包名");
        }

        // ============================================================
        // 加入 / 换包 / 移出
        // ============================================================

        /// <summary>资源拖到某个包上：加入（或从别的包换过来）</summary>
        internal void DropAssets(string bundle, string[] assets)
        {
            int added = ABBundleEditor.AddAssets(bundle, assets, out int moved, out int skipped);
            _pending.Remove(bundle);

            RevABLog.Info($"[RevAB] 加入包「{bundle}」：{added} 个" +
                          (moved > 0 ? $"（其中 {moved} 个是从别的包换过来的）" : "") +
                          (skipped > 0 ? $"，跳过 {skipped} 个（已在包内 / 脚本）" : ""));

            Refresh("工具内加入资源");
            ReselectAfterSync(bundle);
        }

        /// <summary>资源拖到文件夹节点 / 空白处：以第一个资源的名字新建包，全部放进去</summary>
        internal void DropAssetsAsNewBundle(string parent, string[] assets)
        {
            if (assets.Length == 0) return;

            var existing = new HashSet<string>(AllBundleNames());
            string name = ABBundleEditor.MakeUnique(
                ABBundleEditor.Combine(parent, ABBundleEditor.SafeNameFrom(assets[0])), existing);

            DropAssets(name, assets);
        }

        /// <summary>
        /// 把资源移出所在的包。
        /// ★ 自己标记的：直接清掉；继承自文件夹的：单独清不掉 —— 问一句"要不要把那个文件夹的标记清掉"。
        /// </summary>
        internal void RemoveAssets(List<ABAssetTreeView.Row> rows)
        {
            if (rows == null || rows.Count == 0) return;

            var own = new List<string>();
            var folders = new SortedSet<string>();
            foreach (ABAssetTreeView.Row row in rows)
            {
                if (row.Inherited) folders.Add(row.markSource);
                else own.Add(row.assetPath);
            }

            ABGUI.Defer(() =>
            {
                int changed = ABBundleEditor.ClearMarks(own);

                if (folders.Count > 0)
                {
                    string list = string.Join("\n", folders);
                    int choice = EditorUtility.DisplayDialogComplex("有资源的包是继承来的",
                        $"选中的资源里，有 {rows.Count - own.Count} 个的包是从这些文件夹的标记继承来的，单独移不掉：\n\n{list}\n\n" +
                        "清掉文件夹的标记 = 文件夹里的所有资源都退出这个包。",
                        "清掉文件夹标记", "取消", "在 Project 里选中文件夹");

                    if (choice == 0) changed += ABBundleEditor.ClearMarks(folders);
                    else if (choice == 2) ABGUI.SelectInProject(folders);
                }

                if (changed > 0) RevABLog.Info($"[RevAB] 已移出 {changed} 处标记");
                Refresh("工具内移出资源");
            });
        }

        // ============================================================
        // 右键菜单
        // ============================================================

        internal void ShowBundleContextMenu(List<string> paths)
        {
            var menu = new GenericMenu();
            bool single = paths.Count == 1;
            string first = paths.Count > 0 ? paths[0] : null;

            menu.AddItem(new GUIContent("新建包"), false, () => CreateBundle(first == null ? string.Empty
                : _bundleTree.IsBundle(first) ? ABBundleEditor.ParentOf(first) : first));

            if (single) menu.AddItem(new GUIContent("在此文件夹下新建包"), false, () => CreateBundle(first));
            else menu.AddDisabledItem(new GUIContent("在此文件夹下新建包"));

            menu.AddSeparator("");

            if (single) menu.AddItem(new GUIContent("重命名    F2"), false, () => _bundleTree.BeginRenamePath(first));
            else menu.AddDisabledItem(new GUIContent("重命名    F2"));

            menu.AddItem(new GUIContent("删除    Delete"), false, () => DeleteNodes(paths));

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("在 Project 里选中包内资源"), false, () => ABGUI.SelectInProject(AssetsUnder(paths)));
            menu.AddItem(new GUIContent("复制包名"), false, () => EditorGUIUtility.systemCopyBuffer = string.Join("\n", paths));

            menu.ShowAsContext();
        }

        internal void ShowEmptyContextMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("新建包"), false, () => CreateBundle(string.Empty));
            menu.AddItem(new GUIContent("清理未使用包名"), false, CleanupUnusedNames);
            menu.AddItem(new GUIContent("刷新"), false, () => ABMarkerWatcher.RefreshNow("手动刷新"));
            menu.ShowAsContext();
        }

        internal void ShowAssetContextMenu(List<ABAssetTreeView.Row> rows)
        {
            if (rows.Count == 0) return;

            var paths = new List<string>();
            foreach (ABAssetTreeView.Row row in rows) paths.Add(row.assetPath);

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("在 Project 里定位"), false, () => ABGUI.SelectInProject(paths));
            menu.AddItem(new GUIContent("移出包    Delete"), false, () => RemoveAssets(rows));

            // 「移到」：列出所有别的包（多级包名在菜单里自然成为子菜单）
            List<string> bundles = AllBundleNames();
            if (bundles.Count == 0) menu.AddDisabledItem(new GUIContent("移到/（还没有别的包）"));
            foreach (string bundle in bundles)
            {
                string target = bundle;
                menu.AddItem(new GUIContent("移到/" + bundle), false, () => DropAssets(target, paths.ToArray()));
            }

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("复制路径"), false, () => EditorGUIUtility.systemCopyBuffer = string.Join("\n", paths));

            if (rows.Count == 1 && rows[0].Inherited)
            {
                string folder = rows[0].markSource;
                menu.AddItem(new GUIContent("定位标记所在的文件夹"), false, () => ABGUI.Ping(folder));
            }

            menu.ShowAsContext();
        }

        // ============================================================
        // 小工具
        // ============================================================

        /// <summary>这些包 / 包文件夹下的所有资源路径</summary>
        private List<string> AssetsUnder(List<string> paths)
        {
            var list = new List<string>();
            foreach (KeyValuePair<string, List<string>> pair in _collect.bundleToAssets)
            {
                if (!paths.Exists(p => ABBundleEditor.IsUnder(pair.Key, p))) continue;
                foreach (string logic in pair.Value)
                    if (_collect.logicToAssetPath.TryGetValue(logic, out string path)) list.Add(path);
            }
            return list;
        }

        /// <summary>改完标记后：等这次重扫生效，再把选中定位到新位置（重扫前节点还不存在）</summary>
        private void ReselectAfterSync(params string[] paths)
        {
            EditorApplication.delayCall += () =>
            {
                _collect = ABCollectCache.Get();
                _treeDirty = true;
                SyncData();
                _bundleTree.SelectPaths(paths);
                ABMarkerWatcher.RepaintOpenWindows();
            };
        }
    }
}
