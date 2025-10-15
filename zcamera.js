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
                    throw new Error("CameraBridge.exe path is required");
                }

                const resolvedBridgePath = path.isAbsolute(candidateBridgePath)
                    ? candidateBridgePath
                    : path.resolve(__dirname, candidateBridgePath);

                if (!fs.existsSync(resolvedBridgePath)) {
                    throw new Error(`CameraBridge.exe not found: ${resolvedBridgePath}`);
                }

                const outputDir = path.resolve(msg.outputDir || baseOutputDir || process.cwd());
                if (!fs.existsSync(outputDir)) {
                    fs.mkdirSync(outputDir, { recursive: true });
                }

                const rawFilename = msg.filename || baseFilename || "photo-{{timestamp}}.bmp";
                const format = (msg.format || baseFormat || "bmp").toLowerCase();
                const zone = msg.zone;
                const parsedMeterIndex = Number(msg.meterIndex);
                const meterIndex = Number.isFinite(parsedMeterIndex) && parsedMeterIndex > 0 ? parsedMeterIndex : baseMeterIndex;
                const candidateCkDllPath = (msg.ckDllPath || baseCkDllPath || DEFAULT_CK_DLL_PATH || "").toString().trim();

                const filename = buildFilenameWithMeterIndex(rawFilename, meterIndex, msg);
                const outputPath = path.join(outputDir, filename);

                const args = ["--capture", outputPath];
                if (format === "bgr24" || format === "rgb24" || format === "gray8") {
                    args.push("--format", format);
                } else if (format !== "bmp") {
                    node.warn(`Unknown image format ${format}; defaulting to bmp`);
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
                        throw new Error(`CKGenCapture.dll not found: ${resolvedCkDllPath}`);
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

                node.status({ fill: "blue", shape: "dot", text: "capturing" });
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

function buildFilename(pattern, msg) {
    if (typeof pattern !== "string" || pattern.length === 0) {
        pattern = "photo-{{timestamp}}.bmp";
    }

    const timestamp = Date.now().toString();
    return pattern.replace(/\{\{\s*([^{}]+?)\s*\}\}/g, (match, key) => {
        const trimmedKey = key.trim();
        if (trimmedKey === "timestamp") {
            return timestamp;
        }
        if (msg && Object.prototype.hasOwnProperty.call(msg, trimmedKey) && msg[trimmedKey] != null) {
            return String(msg[trimmedKey]);
        }
        return match;
    });
}

function buildFilenameWithMeterIndex(pattern, meterIndex, msg) {
    const resolved = buildFilename(pattern, msg);
    if (!Number.isFinite(meterIndex) || meterIndex <= 0) {
        return resolved;
    }

    const ext = path.extname(resolved);
    const base = ext ? resolved.slice(0, -ext.length) : resolved;
    return `${base}_meter${meterIndex}${ext}`;
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
