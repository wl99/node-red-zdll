const { execFile } = require("child_process");
const fs = require("fs");
const path = require("path");

const DEFAULT_BRIDGE_PATH = path.resolve(__dirname, "bin", "CameraBridge.exe");
const DEFAULT_CK_DLL_PATH = path.resolve(__dirname, "bin", "CKGenCapture.dll");

module.exports = function (RED) {
    "use strict";

    function ZcameraNode(config) {
        RED.nodes.createNode(this, config);
        const node = this;

        const baseOutputDir = config.outputDir || "";
        const baseFilename = config.defaultFilename || "photo-{{timestamp}}.bmp";
        const baseFormat = config.format || "bmp";
        const baseCkDllPath = config.ckDllPath || "";
        const baseMeterIndex = Number(config.meterIndex) > 0 ? Number(config.meterIndex) : 1;

        node.on("input", (msg, send, done) => {
            try {
                const candidateBridgePath = (msg.bridgePath || DEFAULT_BRIDGE_PATH || "").toString().trim();
                if (!candidateBridgePath) {
                    throw new Error("未配置 CameraBridge.exe 路径");
                }

                const resolvedBridgePath = path.isAbsolute(candidateBridgePath)
                    ? candidateBridgePath
                    : path.resolve(__dirname, candidateBridgePath);

                if (!fs.existsSync(resolvedBridgePath)) {
                    throw new Error(`未找到 CameraBridge.exe: ${resolvedBridgePath}`);
                }

                const outputDir = path.resolve(msg.outputDir || baseOutputDir || process.cwd());
                if (!fs.existsSync(outputDir)) {
                    fs.mkdirSync(outputDir, { recursive: true });
                }

                const filename = buildFilename(msg.filename || baseFilename || "photo-{{timestamp}}.bmp");
                const outputPath = path.join(outputDir, filename);
                const format = (msg.format || baseFormat || "bmp").toLowerCase();
                const zone = msg.zone;
                const parsedMeterIndex = Number(msg.meterIndex);
                const meterIndex = Number.isFinite(parsedMeterIndex) && parsedMeterIndex > 0 ? parsedMeterIndex : baseMeterIndex;
                const candidateCkDllPath = (msg.ckDllPath || baseCkDllPath || DEFAULT_CK_DLL_PATH || "").toString().trim();

                const args = ["--capture", outputPath];
                if (format === "bgr24" || format === "rgb24" || format === "gray8") {
                    args.push("--format", format);
                } else if (format !== "bmp") {
                    node.warn(`未知图像格式 ${format}，已使用默认 bmp 输出`);
                }

                if (zone) {
                    const zoneArgs = parseZone(zone);
                    if (zoneArgs.length > 0) {
                        args.push("--zone", ...zoneArgs.map((value) => value.toString()));
                    }
                }

                if (Number.isFinite(meterIndex) && meterIndex > 0) {
                    args.push("--meter-index", meterIndex.toString());
                }

                if (candidateCkDllPath) {
                    const resolvedCkDllPath = path.isAbsolute(candidateCkDllPath)
                        ? candidateCkDllPath
                        : path.resolve(path.dirname(resolvedBridgePath), candidateCkDllPath);

                    if (!fs.existsSync(resolvedCkDllPath)) {
                        throw new Error(`未找到 CKGenCapture.dll: ${resolvedCkDllPath}`);
                    }

                    args.push("--ck-dll", resolvedCkDllPath);
                    msg.ckDllPath = resolvedCkDllPath;
                } else {
                    msg.ckDllPath = "";
                }

                const msgTimeout = Number(msg.timeout);
                const configTimeout = Number(config.timeout);
                const timeout = Number.isFinite(msgTimeout) && msgTimeout > 0
                    ? msgTimeout
                    : Number.isFinite(configTimeout) && configTimeout > 0
                        ? configTimeout
                        : 60000;

                node.status({ fill: "blue", shape: "dot", text: "拍照中" });
                execFile(resolvedBridgePath, args, { timeout }, (error, stdout, stderr) => {
                    if (error) {
                        node.status({ fill: "red", shape: "ring", text: error.message });
                        const err = new Error(stderr || error.message);
                        node.error(err, msg);
                        done(err);
                        return;
                    }

                    msg.payload = {
                        output: outputPath,
                        stdout,
                        stderr,
                        bridgePath: resolvedBridgePath,
                        ckDllPath: msg.ckDllPath,
                        args,
                        timeout,
                        meterIndex
                    };
                    node.status({ fill: "green", shape: "dot", text: path.basename(outputPath) });
                    send(msg);
                    done();
                });
            } catch (err) {
                node.status({ fill: "red", shape: "ring", text: err.message });
                node.error(err, msg);
                done(err);
            }
        });
    }

    RED.nodes.registerType("zcamera", ZcameraNode);
};

function buildFilename(pattern) {
    if (pattern.includes("{{timestamp}}")) {
        return pattern.replace(/\{\{timestamp\}\}/g, Date.now().toString());
    }
    return pattern;
}

function parseZone(value) {
    if (Array.isArray(value)) {
        return value.map(Number).filter((num) => Number.isFinite(num));
    }
    if (typeof value === "string") {
        const parts = value.split(/[\,\s]+/).filter(Boolean);
        return parts.map(Number).filter((num) => Number.isFinite(num));
    }
    return [];
}
