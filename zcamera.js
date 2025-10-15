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

        node.on("input", async (msg, send, done) => {
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
                let format = (msg.format || baseFormat || "bmp").toLowerCase();
                const zone = msg.zone;
                const parsedMeterIndex = Number(msg.meterIndex);
                const meterIndex = Number.isFinite(parsedMeterIndex) && parsedMeterIndex > 0 ? parsedMeterIndex : baseMeterIndex;
                const candidateCkDllPath = (msg.ckDllPath || baseCkDllPath || DEFAULT_CK_DLL_PATH || "").toString().trim();

                if (format !== "bmp" && format !== "bgr24" && format !== "rgb24" && format !== "gray8") {
                    node.warn(`Unknown image format ${format}; defaulting to bmp`);
                    format = "bmp";
                }

                const filenameTemplate = buildFilename(rawFilename, msg);
                const outputTemplate = path.join(outputDir, filenameTemplate);
                const zoneArgs = zone ? parseZone(zone) : [];

                const msgTimeout = Number(msg.timeout);
                const configTimeout = Number(config.timeout);
                const timeout = Number.isFinite(msgTimeout) && msgTimeout > 0
                    ? msgTimeout
                    : Number.isFinite(configTimeout) && configTimeout > 0
                        ? configTimeout
                        : 60000;

                let resolvedCkDllPath = "";
                if (candidateCkDllPath) {
                    const candidateAbsolute = path.isAbsolute(candidateCkDllPath)
                        ? candidateCkDllPath
                        : path.resolve(path.dirname(resolvedBridgePath), candidateCkDllPath);
                    if (!fs.existsSync(candidateAbsolute)) {
                        throw new Error(`CKGenCapture.dll not found: ${candidateAbsolute}`);
                    }
                    resolvedCkDllPath = candidateAbsolute;
                }

                const meterIndexes = buildMeterIndexList(msg.meterIndexes, meterIndex);
                const captureArgs = buildCaptureArgs({
                    outputTemplate,
                    format,
                    zoneArgs,
                    meterIndexes,
                    ckDllPath: resolvedCkDllPath
                });

                node.status({ fill: "blue", shape: "dot", text: `capturing ${meterIndexes.length}` });
                const { stdout, stderr } = await execFileAsync(resolvedBridgePath, captureArgs, timeout);

                const summary = parseBridgeOutput(stdout);
                const resultCount = Array.isArray(summary?.results) ? summary.results.length : 0;

                msg.ckDllPath = resolvedCkDllPath;
                msg.stdout = stdout;
                msg.stderr = stderr;
                msg.args = captureArgs;
                msg.bridge = {
                    manufacturer: summary?.manufacturer,
                    resolution: summary?.resolution,
                    meterCount: summary?.meterCount
                };
                if (resultCount === 1) {
                    msg.payload = summary.results[0];
                } else {
                    msg.payload = summary?.results || [];
                }

                const statusText = resultCount === 1
                    ? path.basename(summary.results[0].output)
                    : `done (${resultCount})`;
                node.status({ fill: "green", shape: "dot", text: statusText });
                send(msg);
                done();
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

function buildMeterIndexList(rawIndexes, fallback) {
    if (Array.isArray(rawIndexes)) {
        const normalized = rawIndexes
            .map((value) => Number(value))
            .filter((value) => Number.isFinite(value) && value > 0);
        if (normalized.length > 0) {
            return Array.from(new Set(normalized));
        }
    }
    return [fallback];
}

function buildCaptureArgs({ outputTemplate, format, zoneArgs, meterIndexes, ckDllPath }) {
    const args = ["--capture", outputTemplate];

    if (format !== "bmp") {
        args.push("--format", format);
    }

    if (zoneArgs.length > 0) {
        args.push("--zone", ...zoneArgs.map((value) => value.toString()));
    }

    if (meterIndexes.length > 1) {
        args.push("--meter-indexes", meterIndexes.join(","));
    } else {
        args.push("--meter-index", meterIndexes[0].toString());
    }

    if (ckDllPath) {
        args.push("--ck-dll", ckDllPath);
    }

    return args;
}

function execFileAsync(command, args, timeout) {
    return new Promise((resolve, reject) => {
        execFile(command, args, { timeout }, (error, stdout, stderr) => {
            if (error) {
                const err = new Error(stderr || error.message);
                err.stdout = stdout;
                err.stderr = stderr;
                err.args = args;
                reject(err);
                return;
            }
            resolve({ stdout, stderr });
        });
    });
}

function parseBridgeOutput(stdout) {
    if (typeof stdout !== "string") {
        throw new Error("Bridge output missing");
    }

    const lines = stdout.split(/\r?\n/);
    for (let i = lines.length - 1; i >= 0; i -= 1) {
        const line = lines[i].trim();
        if (line.startsWith("JSON:")) {
            const json = line.substring(5).trim();
            if (!json) {
                throw new Error("Bridge JSON summary is empty");
            }
            return JSON.parse(json);
        }
    }

    throw new Error("Bridge output missing JSON summary");
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
