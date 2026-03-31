# 龙之书

《龙胤立志传》Mod框架

> 龙，可是帝王之征啊！

龙之书当前主要提供两类能力：

1. 在游戏加载配置表时，导出游戏配置表，便于分析和二次整理。
2. 在不直接改游戏资源的前提下，通过 `ModsOfLong` 目录对 CSV 配置表做增量补丁。

## 配置界面

- 进游戏后会自动弹出一次配置界面。
- 也可以按 `F4` 打开或关闭。
- 改完会自动保存。
- 可以改开关键，也可以关掉“启动后自动打开”。

## 功能与作用

### 1. 配置表导出

启动游戏后，会导出游戏配置表到目录：

```
<游戏目录>\DataDump\Latest
```

### 2. 数据 Mod 增量补丁

Mod 会在游戏 `Mods` 目录下自动创建：

```
<游戏目录>\Mods\ModsOfLong
```

在这个目录下，所有以 `mod` 开头的文件夹都会被识别为一个数据 Mod。

## 使用方法

### 安装 Mod

前提：

1. 已安装《龙胤立志传》
2. 已安装 MelonLoader
3. 已将构建产物放到游戏 `Mods` 目录，或直接使用本项目编译自动复制

运行成功后：

- `TheBookOfLong.dll` 会位于 `<游戏目录>\Mods`
- 导出目录会出现在 `<游戏目录>\DataDump\Latest`
- 数据 Mod 根目录会出现在 `<游戏目录>\Mods\ModsOfLong`

### 编写数据 Mod

数据 Mod 目录结构示例：

```text
Mods
└─ ModsOfLong
   └─ modMyFirstMod
      ├─ Info.json
      └─ Data
         ├─ NameData.csv
         └─ ItemData.csv
```

`Info.json` 可选，示例：

```json
{
  "Name": "我的第一个数据 Mod",
  "Version": "1.0.0"
}
```

字段说明：

1. `Name`：显示名。
2. `Version`：版本号，仅用于展示和日志。

如果没有 `Info.json`：

1. 名称默认使用文件夹名去掉 `mod` 前缀后的内容。
2. 版本号默认显示为 `unspecified`。

### Mod 加载配置

Mod 是否启用、以及多个 Mod 的加载顺序，不再由各自的 `Info.json` 决定，而是由龙之书框架统一维护。

配置文件位置：

```text
<游戏目录>\UserData\TheBookOfLong.ModLoadConfig.json
```

示例：

```json
{
  "FormatVersion": 1,
  "Description": "修改本文件后，需要完全重启游戏才能应用新的 Mod 加载配置。Mods 数组中的顺序就是加载顺序，越靠后覆盖能力越强。",
  "Mods": [
    {
      "FolderName": "modMyFirstMod",
      "DisplayName": "我的第一个数据 Mod",
      "Version": "1.0.0",
      "Enabled": true
    }
  ]
}
```

规则说明：

1. `Mods` 数组中的顺序就是加载顺序。
2. 越靠后的 Mod 越晚加载，因此覆盖能力越强。
3. `Enabled` 由框架读取，用来决定该 Mod 是否参与当前启动流程。
4. 修改这个文件后，玩家需要完全重启游戏，新的加载配置才会生效。

### CSV 补丁规则

补丁文件路径应与导出的配置名保持一致，例如导出得到：

```text
DataDump\Latest\Data\NameData.csv
```

则补丁文件应放在：

```text
Mods\ModsOfLong\modMyFirstMod\Data\NameData.csv
```

合并规则：

1. 第一行表头必须与原表完全一致。
2. 主键列会优先识别 `id`、`ID`、`编号`、`序号`、`剧情编号` 等列名。
3. 主键相同则覆盖原行。
4. 主键不存在则追加新行。
5. 没有明确主键时，会退回到更宽松的首列识别逻辑。

### 字符串 ID 规则

由于游戏的数据配置ID是递增的，为了使得Mod添加内容方便，新增一种可以使用字符串作为ID占位符的方式

```text
mod角色A
mod新剧情001
modMyCustomNpc
```

处理规则：

1. 以 `mod` 开头的字符串主键会被识别为“符号 ID”。
2. 所有数据 Mod 读取完成后，会按字符串排序统一映射为新的数字 ID。
3. 如果同一个符号 ID 出现在多个表中，它们会映射成同一个数字 ID。
4. 如果某些共享符号 ID 的表缺少对应行，Mod 会自动补空白占位行并在日志里提示。

该机制使得多个Mod添加相同内容不易出现ID冲突和覆盖问题。

### 游戏复杂数据 JSON 补丁规则

对于导出的复杂对象数据，可在 Mod 目录中放置与导出名一致的 JSON 文件：

```text
Mods\ModsOfLong\modMyFirstMod\ComplexData\WorldPlotEventController_WorldPlotEventDataBase.json
Mods\ModsOfLong\modMyFirstMod\ComplexData\MissionDataController_MainMissionDataBase.json
```

当前支持以下文件：

**游戏任务配置**
- `MissionDataController_bountyMissionDataBase.json`
- `MissionDataController_BranchMissionDataBase.json`
- `MissionDataController_LittleMissionDataBase.json`
- `MissionDataController_MainMissionDataBase.json`
- `MissionDataController_SpeKillerMissionDataBase.json`
- `MissionDataController_TreasureMapMissionDataBase.json`

**游戏触发器配置**
- `WorldPlotEventController_WorldPlotEventDataBase.json`

处理规则：

1. 四个 `MissionDataController_*MissionDataBase.json` 列表文件和 `WorldPlotEventController_WorldPlotEventDataBase.json` 使用 JSON 数组补丁。
2. 数组内对象按 `name` 字段匹配。
3. `name` 相同则覆盖原对象。
4. `name` 不同则追加新对象。
5. `MissionDataController_SpeKillerMissionDataBase.json` 与 `MissionDataController_TreasureMapMissionDataBase.json` 使用单个 JSON 对象直接覆盖原值。

### 剧情相关 plotID 字段

在 `MissionData` 与 `WorldPlotEventData` 的 `plotID` 字段中，也可以使用字符串占位符：

```json
{
  "plotID": "modMyPlot001"
}
```

处理规则与 CSV 的字符串 ID 一致：

1. `plotID` 中以 `mod` 开头的字符串会被识别为符号 ID。
2. 它会按 `GameData\PlotData.csv` 的最大现有 ID 继续分配新的数字 ID。
3. 如果同一个符号 ID 同时出现在 CSV 与复杂对象 JSON 中，它们会映射成同一个数字 ID。

所有符号 ID 的引用情况和最终映射会写入：

```text
DataDump\Latest\SymbolicFieldReport.json
```

## Build

### 准备工作

1. 安装 `.NET 6 SDK`
2. 安装《龙胤立志传》
3. 安装 MelonLoader
4. 确认 `Directory.Build.props` 中的游戏路径正确

当前路径配置位于：

```xml
<LongYinGameDir>E:\SteamLibrary\steamapps\common\LongYinLiZhiZhuan</LongYinGameDir>
```

如果你的游戏不在这个目录，需要先改成你本机的实际路径。

### 编译

```powershell
dotnet build D:\codes\LongYin\TheBookOfLong\TheBookOfLong.sln
```

项目在构建完成后会自动把以下文件复制到游戏 `Mods` 目录：

- `TheBookOfLong.dll`
- `TheBookOfLong.pdb`

## 项目结构

```text
TheBookOfLong
├─ Directory.Build.props
├─ TheBookOfLong.sln
└─ src
   └─ TheBookOfLong
      ├─ MainMod.cs
      ├─ ConfigDumpManager.cs
      ├─ ConfigDumpPatches.cs
      ├─ DataModManager.cs
      ├─ GameComplexDataPatchManager.cs
      ├─ GameComplexDataDumpManager.cs
      ├─ CsvPatchFile.cs
      └─ Mods
         ├─ ModProjectRegistry.cs
         ├─ ModLoadConfigManager.cs
         ├─ Csv
         │  ├─ CsvUtility.cs
         │  ├─ CsvPatchApplier.cs
         │  └─ PlotDataPatchApplier.cs
         ├─ ComplexData
         │  ├─ ComplexPatchModels.cs
         │  ├─ ComplexTypeAccessor.cs
         │  ├─ ComplexJsonValuePatcher.cs
         │  └─ ComplexRuntimePatchApplier.cs
         └─ Symbolic
            └─ SymbolicIdService.cs
```

核心文件说明：

- `MainMod.cs`：MelonLoader 入口，初始化导出、Mod 注册表和补丁系统，并注册 Harmony 补丁。
- `ConfigDumpManager.cs`：负责配置导出、编码识别、命名和 `manifest.json` 生成。
- `ConfigDumpPatches.cs`：负责挂接游戏加载流程与 Unity 资源读取流程。
- `ModProjectRegistry.cs`：负责扫描 `ModsOfLong` 并生成统一的 Mod 项目视图。
- `ModLoadConfigManager.cs`：负责维护 `<游戏目录>\UserData\TheBookOfLong.ModLoadConfig.json`，决定 Mod 是否启用以及加载顺序。
- `DataModManager.cs`：负责把已启用 Mod 的 CSV 补丁接入游戏文本资源。
- `GameComplexDataPatchManager.cs`：负责把已启用 Mod 的复杂 JSON 补丁回写到场景内控制器对象。
- `SymbolicIdService.cs`：负责统一分配 `modXXX` 符号 ID，并让 CSV 与 ComplexData 共享同一套映射。

## 当前状态

当前项目仍处于早期开发阶段，重点在：

- 还原游戏原始配置表文本与表名
- 建立稳定的数据 Mod 增量修改流程
- 为后续更完整的游戏逻辑分析和内容扩展打基础
