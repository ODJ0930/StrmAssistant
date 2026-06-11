# Strm Assistant Lite 兼容构建

这是基于原项目 [sjtuross/StrmAssistant](https://github.com/sjtuross/StrmAssistant) 的个人兼容构建。

本仓库仅记录本次针对新版 Emby Server 的兼容性修改，保留并尊重原作者的工作成果、项目结构和许可证。原项目的完整功能说明、使用文档和历史更新请以原仓库为准。

## 本次修改

- 兼容 Emby Server 4.9.3.0 和 4.10.0.14 的部分内部 API 变化。
- 通过反射适配 `GetStaticMediaSources`、媒体挂载、外部字幕扫描、指纹处理和缩略图刷新等接口签名变化。
- 调整构建配置，使插件可以在 Linux .NET SDK 容器中构建，同时保留 Windows 下原有的 PostBuild 行为。
- Release 只提供单文件 `StrmAssistantLite.dll`，已合并必要依赖，便于直接部署。

## 验证环境

已在以下容器中验证插件加载和核心任务：

- `amilys/embyserver:4.9.3.0`
- `emby/embyserver:4.10.0.14-debug-amd64`

验证内容包括：

- 插件加载和配置页注册
- `Extract MediaInfo`
- `Persist MediaInfo`
- `Scan External Subtitles`
- `Merge Multi Versions`
- `mp4 + srt + strm` 测试媒体扫描

## 安装

1. 从 Releases 下载 `StrmAssistantLite.dll`。
2. 将该文件放入 Emby Server 的 `plugins` 目录。
3. 重启 Emby Server。
4. 在 Emby 插件页面确认 `Strm Assistant` 已加载。

## 原创声明

Strm Assistant 的原创工作属于原作者和原项目贡献者。本仓库只是为了个人使用场景补充兼容性修改，不声称拥有原项目的原创成果，也不替代原项目发布。

如果需要了解完整功能、授权、使用说明或后续官方支持，请访问原项目：

[https://github.com/sjtuross/StrmAssistant](https://github.com/sjtuross/StrmAssistant)
