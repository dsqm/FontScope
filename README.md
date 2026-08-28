# FontScope

字符字体查询工具 —— 输入一个或多个字符，找出本机所有**支持显示该字符**的字体。

适用于：找生僻字的可用字体、检查某个符号（如特殊标点、Emoji、扩展区汉字）有哪些字体覆盖、为排版挑选合适字重的字体等场景。

## 赞助

https://docs.qq.com/aio/DRWtMY3FQS0ZHRGRG  

## 功能特性

- **字符反查字体**：输入任意字符（含扩展 B/C 区等生僻码位），列出所有字形覆盖该字符的字体
- **占坑探测**：自动识别「声称支持但实际画出空白/豆腐块」的字体并如实标注
- **实时字形预览**：结果列表内直接渲染目标字符的实际字形（Direct2D 渲染）
- **多维度信息列**：字体名 / 子族、样式、字重、格式（TTF/OTF/TTC…）、PostScript 名、来源，各列均可排序
- **多来源扫描**：系统字体、当前用户字体、自定义文件夹；可在「扫描范围」中勾选启用/排除任意来源
- **TTC 支持**：正确解析 TrueType Collection 并区分其中各个 face

## 环境要求

- Windows 7 及以上（x64）
  *注意 Windows 7 不支持渲染彩色emoji*

- 运行：[.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0)
- 编译：.NET 6 SDK

## 构建与发布

```bash
# 编译调试版
dotnet build

# 发布框架依赖版（体积小，目标机需装 .NET 6 Desktop Runtime）
dotnet publish FontScope.csproj -c Release -r win-x64 --self-contained false -o publish/framework-dependent

# 发布自包含单文件版（零依赖）
dotnet publish FontScope.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/singlefile
```

## 使用说明

1. 启动后程序自动扫描已启用的字体来源（首次启动稍慢，之后走内存缓存）
2. 在顶部输入框键入要查询的字符，回车或点「查询」
3. 结果列表按列排序，点击列头切换升/降序  
4. 通过「添加文件夹…」和「扫描范围」管理参与检索的字体目录  

## 开源协议

本项目基于 [GPL-3.0](LICENSE)（GNU 通用公共许可证第 3 版）开源发布。
