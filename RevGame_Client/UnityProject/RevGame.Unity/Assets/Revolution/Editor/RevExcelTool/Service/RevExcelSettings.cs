// ============================================================
// RevExcelSettings.cs —— 导表工具的两份设置：团队共享的 / 只属于自己的
//
// 位置：Editor\RevExcelTool\Service\
//
// 【为什么分两份】
//   · 输出到哪（代码目录、数据目录、文件名）是**工程结构**：全队必须一样，否则 A 导到这、B 导到那，
//     工程里出现两份 RevDataTables.cs 直接编译不过 → 存 ProjectSettings/（进版本库）；
//   · Excel 在哪是**每个人自己机器上的事**：策划表常放在另一个仓库 / 网盘，每人路径都不同
//     → 存 UserSettings/（Unity 默认不进版本库）。
//   WPF 版把这几样都存在本机 APPDATA 里（它和 Unity 工程无关）；编辑器版能分清就分清。
//
// 【为什么用 ScriptableSingleton】Unity 自带的"工程级单例设置"：
//   按 FilePath 读写、不进 Assets（不产生导入 / 不会被打进包）、改完调 Save 就落盘。
// ============================================================
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor.ExcelTool
{
    /// <summary>团队共享的导表设置（ProjectSettings/RevExcelToolSettings.asset）</summary>
    [FilePath("ProjectSettings/RevExcelToolSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class RevExcelProjectSettings : ScriptableSingleton<RevExcelProjectSettings>
    {
        /// <summary>
        /// 代码默认目录：框架自带的 Generation 程序集（已引用 Revolution.Runtime、autoReferenced，业务直接能用）。
        /// 和 RevResPath.cs 在一起 —— "生成物都在一个地方"。
        /// </summary>
        public const string DefaultCodeDir = "Assets/Revolution/Generation";

        public const string DefaultStructFileName = "RevDataStructures.cs";
        public const string DefaultContainerFileName = "RevDataTables.cs";

        [Tooltip("数据结构类（struct）的输出目录")]
        public string structDir = DefaultCodeDir;

        [Tooltip("容器类（XxxTable）的输出目录")]
        public string containerDir = DefaultCodeDir;

        [Tooltip("数据结构类文件名")]
        public string structFileName = DefaultStructFileName;

        [Tooltip("容器类文件名")]
        public string containerFileName = DefaultContainerFileName;

        [Tooltip("数据文件目录。留空 = 跟随资源根目录：<资源根目录>/Data（运行时按逻辑路径 Data/<表名> 读，推荐留空）")]
        public string dataDir = "";

        [Tooltip("数据文件有新增 / 删除时，导出后自动生成资源映射（ResMap.txt）—— 否则 AB 模式 / 真机读不到新表")]
        public bool generateMapAfterExport = true;

        public void SaveNow() => Save(true);
    }

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
