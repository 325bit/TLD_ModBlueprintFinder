# 漫漫长夜mod配方搜索器 · TLD Mod Blueprint Finder

![version](https://img.shields.io/badge/version-1.3.0-informational)

![game](https://img.shields.io/badge/The%20Long%20Dark-2.55-success)

![platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4)

![license](https://img.shields.io/badge/license-MIT-blue)

---

## 使用

1. 确认顶部路径指向游戏根目录（里面要有 `Mods` 文件夹），点「扫描 Mods」  
   —— 之后每次启动会自动沿用上次的路径并直接扫描，不用重复点
2. 在「物品 ID」里输入，按回车（例如 `GEAR_Hammer`、`GEAR_ScrapMetal`、`GEAR_metallighter`）
3. 四个分页就是四组结果：**作为产物 / 作为原料 / 作为工具 / 物品定义**。点任意一行，  
   右侧显示完整原料清单、制作条件和原始数据
4. 双击一行复制它的摘要；「导出 CSV」把当前结果存成表格

| 匹配方式 | 行为                                               |
| ---- | ------------------------------------------------ |
| 自动   | 先精确、再前缀、再包含。拼不全也能查，但补出来的行会用斜体橙字标出，鼠标悬停会说明是怎么匹配上的 |
| 仅精确  | 只认完全相同的 ID                                       |
| 模糊   | ID 里含这段字符就列出                                     |

## 物品定义

第四个分页是**这个物品的 JSON定义文件**。这些文件放在 modcomponent 的 `auto-mapped/` 文件夹里（`notes/`、`collectibles/`、`ClothingItems/` 里也放同格式的定义）。

**「判定依据」列说明这个 ID 是怎么认定的**，因为定义文件里只写文件名、不写物品 ID：

| 判定依据       | 意思                        |
| ---------- | ------------------------- |
| 同 mod 配方引用 | 这个 mod 自己的配方里出现过这个 ID，最可信 |
| 其他 mod 引用  | 别的 mod 的配方引用了它，通常也是同一件物品  |
| 仅文件名       | 没有任何配方引用过，只按文件名对上，前缀不确定   |

大小写：`GEAR_sugara`（某个 mod 加的糖）和 `GEAR_SugarA`（原版糖）去掉前缀后都是 `sugara`。工具优先挑大小写完全一致的，不一致时会在「判定依据」里标一句「大小写不同」。

## 修改与备份

选中左侧任意一行，右侧详情栏上有一排按钮：

| 按钮              | 作用                                                    |
| --------------- | ----------------------------------------------------- |
| 编辑 JSON         | 把这一条的原始 JSON 变成可编辑状态（配方和物品定义都能改），改完点「保存修改」 |
| 保存修改            | 校验 JSON 后写回 modcomponent；解析不过会先问你要不要硬存                 |
| 备份该 modcomponent | 只备份当前行所属的 modcomponent，不做任何修改                          |
| 自动备份            | 勾选时「保存修改」会先备份一份再写（默认勾选，状态会记住）                         |

备份放在 exe 同目录的 `TLDMBF_modcomponent_backup\`，文件名是 `<mod名>.modcomponent-<年月日_时分秒>`，改多少次都不会互相覆盖。整个容器是复制过去的，想还原直接把备份改回 `xxx.modcomponent` 丢回 `Mods` 即可。

写入的做法是把 .modcomponent 整个重写一遍再替换，中途失败原包不会被动过。但还是**建议先关掉游戏**：游戏占着文件时写不进去，工具会直接报错；改完也要重启游戏，modcomponent 才会被重新加载。

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
  <tr>      
    <td align="center" colspan="2"><img src="docs/img/screenshot-definition.png" alt="物品定义"></td>      
  </tr>      
  <tr>      
    <td align="center" colspan="2"><code>GEAR_metallighter</code>：右侧直接贴出它在 <code>auto-mapped/</code> 里的 JSON</td>      
  </tr>      
  <tr>      
    <td align="center" colspan="2"><img src="docs/img/screenshot-edit.png" alt="编辑 JSON"></td>      
  </tr>      
  <tr>      
    <td align="center" colspan="2">「编辑 JSON」：直接改这一条，点「保存修改」写回 modcomponent；勾了自动备份会先留一份</td>      
  </tr>      
</table>

## 许可证

本项目基于 [MIT License](LICENSE) 发布。
