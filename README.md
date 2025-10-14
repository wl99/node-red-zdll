# node-red-zdll

该 Node-RED 插件通过外部 `CameraBridge.exe` 进程调用 32 位相机驱动 `CKGenCapture.dll`，在 64 位 Node.js 环境下实现拍照功能。包内已包含预编译的 `CameraBridge.exe`（x86，依赖本机 .NET 6 运行时）。

## 目录结构

- `package.json` Node-RED 插件描述
- `zdll.js` 运行时逻辑，调用 CameraBridge 并输出拍照结果
- `zdll.html` 编辑器界面，配置桥接程序路径、输出目录等
- `bin/` 预置的 `CameraBridge.exe` 及其运行时配置文件
- `CameraBridge/` 32 位 C# 桥接程序源码，如需自定义可在 Windows 上重新编译

## 使用步骤

1. 确保 `CKGenCapture.dll` 与插件内 `bin/CameraBridge.exe` 位于同一目录（可将 DLL 复制到 `bin/`）。若许可不允许分发，请按厂商要求单独获取。
2. 将本插件放入 Node-RED 的 `~/.node-red/node_modules/` 或通过 `npm install` 安装。`package.json` 默认会将 `bin/` 一并发布。
3. 在 Node-RED 编辑器中拖入 `zdll` 节点，默认会使用 `bin/CameraBridge.exe`；如需替换，可在节点配置或 `msg.bridgePath` 中指定其它路径。
4. 设置输出目录、默认文件名（支持 `{{timestamp}}` 占位符），以及可选的 `gray8/bgr24/rgb24` 输出格式或拍摄区域。
5. 输入消息触发拍照，可额外传入 `msg.filename`、`msg.format`、`msg.zone`、`msg.timeout` 等覆盖配置；执行结果写入 `msg.payload`。

## 重新编译桥接程序

若需要修改或重新构建 `CameraBridge.exe`，可在 Windows 上进入 `CameraBridge/`，执行：

```bash
dotnet publish -c Release -r win-x86 --self-contained false
```

生成的文件位于 `bin/Release/net6.0/win-x86/publish/`，复制其中的 `CameraBridge.exe`、`.dll`、`.deps.json`、`.runtimeconfig.json` 等到插件 `bin/` 即可。
