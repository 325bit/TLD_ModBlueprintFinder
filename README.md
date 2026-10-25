# 漫漫长夜mod配方搜索器 · TLD Mod Blueprint Finder

输入一个mod物品 ID，查找它的合成关系：原料、产品和工具

*A read-only blueprint lookup tool for modded The Long Dark*

![version](https://img.shields.io/badge/version-1.1.0-informational)

![game](https://img.shields.io/badge/The%20Long%20Dark-2.55-success)

![platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4)

![license](https://img.shields.io/badge/license-MIT-blue)

---

**步骤**

1. 下载 `TLD_ModBlueprintFinder.exe`（约 260 KB）
2. 放到任意位置，双击运行。免安装，不写注册表，不建后台服务
3. 首次启动会自动寻找游戏目录；找不到就点「浏览…」，选到 `...\steamapps\common\TheLongDark` 这一层

**卸载**：删掉 exe 就行。唯一的残留是 `%APPDATA%\TLD_ModBlueprintFinder\settings.json`（只记上次选的路径），一并删除即可。

## 使用

1. 确认顶部路径指向游戏根目录（里面要有 `Mods` 文件夹），点「扫描 Mods」  
   —— 之后每次启动会自动沿用上次的路径并直接扫描，不用重复点
2. 在「物品 ID」里输入，按回车（例如 `GEAR_Hammer`、`GEAR_ScrapMetal`）
3. 三个分页就是上面那三组结果；点任意一行，右侧显示这份配方的完整原料清单和原始数据
4. 双击一行复制它的摘要；「导出 CSV」把当前分页的结果存成表格

**输入不用打全**，也**不用管大小写**（`gear_hammer` 一样能查）：

| 匹配方式 | 行为                                               |
| ---- | ------------------------------------------------ |
| 自动   | 先精确、再前缀、再包含。拼不全也能查，但补出来的行会用斜体橙字标出，鼠标悬停会说明是怎么匹配上的 |
| 仅精确  | 只认完全相同的 ID                                       |
| 模糊   | ID 里含这段字符就列出                                     |

## 界面

<table align="center">  
  <tr>  
    <td align="center"><img src="docs/img/screenshot-ingredient.png" alt="作为原料"></td>  
    <td align="center"><img src="docs/img/screenshot-tool.png" alt="作为工具"></td>  
  </tr>  
  <tr>  
    <td align="center"><code>GEAR_ScrapMetal</code>：28 个配方会消耗它</td>  
    <td align="center"><code>GEAR_Hammer</code>：26 个配方拿它当工具</td>  
  </tr>  
</table>

## 常见问题

**查不到某个物品的配方？**

按这三种情况对照一下：

1. **原版物品**：游戏本体的配方是游戏资产，不在任何 mod 文件里。所以查 `GEAR_Knife` 这类原版物品可能显示「无匹配」——  
   这是预期行为，不是工具坏了。本工具只负责 **mod 添加的配方**。
2. **DLL 型 mod**：只提供 dll、不提供配方文件的 mod 不会出现在结果里。
3. **路径选错了**：没指到含 `Mods` 的那一层。工具会告诉你扫到几个容器、几条蓝图，数字是 0 就是路径的问题。

## 许可证

本项目基于 [MIT License](LICENSE) 发布。
