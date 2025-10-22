# node-red-zcamera

该 Node-RED 插件通过外部 `CameraBridge.exe` 进程调用 32 位相机驱动 `CKGenCapture.dll`，在 64 位 Node.js 环境下实现拍照功能。包内已包含预编译的 `CameraBridge.exe`（x86，依赖本机 .NET 6 运行时）。

## 目录结构

- `package.json` Node-RED 插件描述
- `zcamera.js` 运行时逻辑，调用 CameraBridge 并输出拍照结果
- `zcamera.html` 编辑器界面，配置输出目录、默认文件名等
- `bin/` 预置的 `CameraBridge.exe` 及其运行时配置文件
- `CameraBridge/` 32 位 C# 桥接程序源码，如需自定义可在 Windows 上重新编译

## 使用步骤

1. 确保 `CKGenCapture.dll` 与插件内 `bin/CameraBridge.exe` 位于同一目录（可将 DLL 复制到 `bin/`）。若许可不允许分发，请按厂商要求单独获取。
2. 将本插件放入 Node-RED 的 `~/.node-red/node_modules/` 或通过 `npm install` 安装。`package.json` 默认会将 `bin/` 一并发布。
3. 在 Node-RED 编辑器中拖入 `zcamera` 节点，默认固定使用插件内的 `bin/CameraBridge.exe` 与 `bin/CKGenCapture.dll`；如需替换，可在节点面板中填写新的路径，或在消息里通过 `msg.bridgePath` / `msg.ckDllPath` 指定。若留空 `CKGenCapture.dll` 路径，桥接程序会自动回退到 `bin/CKGenCapture.dll`。
4. 节点默认会将照片保存到 `D:\Picture`，文件名模板为 `{{barCode}}_{{meter}}_{{photoType}}.jpg`。模板语法支持 `{{timestamp}}` 以及任意 `msg` 字段（如 `{{orderId}}`），其中 `{{meter}}` 会在多表位拍照时被实际表位号替换。
5. 触发消息拍照时，可额外传入 `msg.barCode`、`msg.photoType`、`msg.filename`、`msg.format`（默认为 `jpg`）、`msg.meterIndex`（单张）或 `msg.meterIndexes`（数组，多张），`msg.timeout`，以及（如需覆盖默认测点配置）`msg.zone`。执行成功后，`msg.payload` 将返回与 C# `PhotoData` 对齐的结构（单张为对象，多张为数组），包含 `PhotoPath`、`MeterPosition`、`PhotoType`、`DataType` 等字段。

### CameraBridge 命令行

```text
CameraBridge.exe --capture <outputPath> [--format gray8|bgr24|rgb24] [--zone <n1> <n2> ...] [--meter-index <n>] [--meter-indexes n1,n2,...] [--ck-dll <path>]
CameraBridge.exe --init [--ck-dll <path>]
CameraBridge.exe --info [--ck-dll <path>]
CameraBridge.exe --release [--ck-dll <path>]
```

- 驱动返回码为 `1` 时表示初始化/拍照成功（程序会换算为退出码 `0`），其他数值请参考厂家文档。
- `--zone` 参数通常按测点数量提供若干整数，缺省时自动填充为 `1`。
- `--meter-index` 表示从第几个测点写出文件（默认 1），`--meter-indexes` 可一次传入多个测点；当输出路径包含 `{{meter}}` 时会替换为测点编号，否则自动追加 `_meter{n}`。
- 输出路径以 `.jpg` 或 `.jpeg` 结尾时会自动编码为 JPEG，`.bmp` 结尾编码为 BMP，其他扩展名将直接写入原始像素数据。
- `--ck-dll` 可显式指定 `CKGenCapture.dll` 位置，便于与驱动安装目录或新版 DLL 配合使用。

## 重新编译桥接程序

若需要修改或重新构建 `CameraBridge.exe`，可在 Windows 上进入 `CameraBridge/`，执行：

```bash
dotnet publish -c Release -r win-x86 --self-contained false
```

生成的文件位于 `bin/Release/net6.0/win-x86/publish/`，复制其中的 `CameraBridge.exe`、`CameraBridge.dll`、`.deps.json`、`.runtimeconfig.json`，以及依赖的 `System.Drawing.Common.dll`、`Microsoft.Win32.SystemEvents.dll` 等到插件 `bin/` 即可。

## 常见问题

- `PhotoCaptureInit 调用失败 (返回码: 1)`：该型号驱动以 `1` 表示成功；桥接程序已自动处理，但如果命令行仍提示失败，请确认相机驱动、授权文件与配置（如 `GenCapture.ini`）齐备后重试。
- `CKGenCapture.dll` 需与相机厂家提供的所有依赖位于同一目录，并在 32 位 Windows 环境中执行；如缺少 VC++ 运行库或设备驱动，同样会导致初始化失败。
