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
  "Name": "我的第一个数据 Mod"
}
```

如果没有 `Info.json`，则默认使用文件夹名去掉 `mod` 前缀后的内容作为显示名。

### CSV 补丁规则

补丁文件路径应与导出的配置名保持一致，例如导出得到：

```text
DataDump\Latest\GameData\NameData.csv
```

则补丁文件应放在：

```text
Mods\ModsOfLong\modMyFirstMod\Data\GameData\NameData.csv
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
      └─ DataModManager.cs
```

核心文件说明：

- `MainMod.cs`：MelonLoader 入口，初始化导出管理器、数据 Mod 管理器，并注册 Harmony 补丁。
- `ConfigDumpManager.cs`：负责配置导出、编码识别、命名和 `manifest.json` 生成。
- `ConfigDumpPatches.cs`：负责挂接游戏加载流程与 Unity 资源读取流程。
- `DataModManager.cs`：负责扫描 `ModsOfLong`、加载 CSV 补丁、字符串 ID 映射与合并回写。

## 当前状态

当前项目仍处于早期开发阶段，重点在：

- 还原游戏原始配置表文本与表名
- 建立稳定的数据 Mod 增量修改流程
- 为后续更完整的游戏逻辑分析和内容扩展打基础
