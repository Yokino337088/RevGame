// ============================================================
// RevExcelUserSettings.cs —— 只属于自己的导表设置（Excel 在哪）
//
// 【为什么必须单独成文件】ScriptableSingleton 靠脚本引用才能从磁盘读回。Unity 一个 .cs 文件只把脚本
//   绑定给其中一个类，以前它和 RevExcelProjectSettings 写在同一文件里，落盘时 m_Script 是空的：
//   文件写得好好的，但编辑器一重启就读不回来，Excel 来源每次开机都被清空。
//   文件名必须和类名一致，不要再合并回去。
// ============================================================
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor.ExcelTool
{
    /// <summary>只属于自己的导表设置（UserSettings/RevExcelTool.asset，不进版本库）</summary>
    [FilePath("UserSettings/RevExcelTool.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class RevExcelUserSettings : ScriptableSingleton<RevExcelUserSettings>
    {
        [Tooltip("Excel 源：.xlsx 文件或装着 .xlsx 的文件夹（可以多个；绝对路径，或相对工程根目录）")]
        public List<string> sources = new List<string>();

        [Tooltip("来源是文件夹时，连子文件夹里的 .xlsx 一起读")]
        public bool includeSubfolders;

        [Tooltip("Excel 保存后自动重新读取（窗口开着时每 1.5 秒看一次文件修改时间，很便宜）")]
        public bool autoReload = true;

        public void SaveNow() => Save(true);
    }
}
