# 漫漫长夜mod配方搜索器 · TLD Mod Blueprint Finder

![version](https://img.shields.io/badge/version-1.2.0-informational)

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

大小写也当回事：`GEAR_sugara`（某个 mod 加的糖）和 `GEAR_SugarA`（原版糖）去掉前缀后都是 `sugara`。  
工具优先挑大小写完全一致的，不一致时会在「判定依据」里标一句「大小写不同」。

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
</table>

## 许可证

本项目基于 [MIT License](LICENSE) 发布。
