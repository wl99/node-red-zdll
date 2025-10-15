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
                const results = [];

                for (let idx = 0; idx < meterIndexes.length; idx += 1) {
                    const currentMeter = meterIndexes[idx];
                    const filename = buildFilenameWithMeterIndex(rawFilename, currentMeter, msg);
                    const outputPath = path.join(outputDir, filename);

                    const captureArgs = ["--capture", outputPath];
                    if (format !== "bmp") {
                        captureArgs.push("--format", format);
                    }

                    if (zoneArgs.length > 0) {
                        captureArgs.push("--zone", ...zoneArgs.map((value) => value.toString()));
                    }

                    captureArgs.push("--meter-index", currentMeter.toString());

                    if (resolvedCkDllPath) {
                        captureArgs.push("--ck-dll", resolvedCkDllPath);
                    }

                    node.status({ fill: "blue", shape: "dot", text: `capturing ${idx + 1}/${meterIndexes.length}` });
                    const { stdout, stderr } = await execFileAsync(resolvedBridgePath, captureArgs, timeout);

                    results.push({
                        output: outputPath,
                        stdout,
                        stderr,
                        bridgePath: resolvedBridgePath,
                        ckDllPath: resolvedCkDllPath,
                        args: captureArgs,
                        timeout,
                        meterIndex: currentMeter
                    });
                }

                msg.ckDllPath = resolvedCkDllPath;
                msg.payload = results.length === 1 ? results[0] : results;
                node.status({ fill: "green", shape: "dot", text: `done (${results.length})` });
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

function buildFilenameWithMeterIndex(pattern, meterIndex, msg) {
    const resolved = buildFilename(pattern, msg);
    if (!Number.isFinite(meterIndex) || meterIndex <= 0) {
        return resolved;
    }

    const ext = path.extname(resolved);
    const base = ext ? resolved.slice(0, -ext.length) : resolved;
    return `${base}_meter${meterIndex}${ext}`;
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
