const { execFile } = require("child_process");
const fs = require("fs");
const path = require("path");

const DEFAULT_BRIDGE_PATH = path.resolve(__dirname, "bin", "CameraBridge.exe");
const DEFAULT_CK_DLL_PATH = path.resolve(__dirname, "bin", "CKGenCapture.dll");
const DEFAULT_OUTPUT_DIR = "D:\\\\Picture";
const DEFAULT_FILENAME_TEMPLATE = "{{barCode}}_{{meter}}_{{photoType}}.jpg";
const DEFAULT_FORMAT = "jpg";

module.exports = function (RED) {
    "use strict";

    function ZcameraNode(config) {
        RED.nodes.createNode(this, config);
        const node = this;

        const baseOutputDir = config.outputDir || DEFAULT_OUTPUT_DIR;
        const baseFilename = config.defaultFilename || DEFAULT_FILENAME_TEMPLATE;
        const baseFormat = config.format || DEFAULT_FORMAT;
        const baseCkDllPath = config.ckDllPath || "";
        const baseMeterIndex = Number(config.meterIndex) > 0 ? Number(config.meterIndex) : 1;
        const baseConfiguredMeterIndexes = parseMeterIndexes(config.meterIndexes);

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

                const rawFilename = msg.filename || baseFilename || DEFAULT_FILENAME_TEMPLATE;
                const requestedFormat = (msg.format || baseFormat || DEFAULT_FORMAT).toString().toLowerCase();
                const { outputFormat, bridgeFormat } = resolveFormats(requestedFormat);
                const zone = msg.zone;
                const parsedMeterIndex = Number(msg.meterIndex);
                const meterIndex = Number.isFinite(parsedMeterIndex) && parsedMeterIndex > 0 ? parsedMeterIndex : baseMeterIndex;
                const candidateCkDllPath = (msg.ckDllPath || baseCkDllPath || DEFAULT_CK_DLL_PATH || "").toString().trim();

                const rawMeterIndexes = msg.meterIndexes !== undefined ? msg.meterIndexes : baseConfiguredMeterIndexes;
                const meterIndexes = buildMeterIndexList(rawMeterIndexes, meterIndex);
                const primaryMeterIndex = meterIndexes[0];
                const resolvedPhotoType = resolvePhotoType(msg);
                const resolvedBarCode = resolveBarCode(msg);

                const templateContext = {
                    ...msg,
                    barCode: resolvedBarCode,
                    meterPosition: primaryMeterIndex,
                    meter: primaryMeterIndex,
                    photoType: resolvedPhotoType
                };
                const filenameTemplate = buildFilename(rawFilename, templateContext);
                const outputTemplate = ensureExtension(path.join(outputDir, filenameTemplate), outputFormat);
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

                const captureArgs = buildCaptureArgs({
                    outputTemplate,
                    bridgeFormat,
                    zoneArgs,
                    meterIndexes,
                    ckDllPath: resolvedCkDllPath
                });

                msg.format = outputFormat;
                msg.barCode = resolvedBarCode;
                msg.photoType = resolvedPhotoType;
                msg.meterPosition = primaryMeterIndex;
                msg.meterIndexes = meterIndexes;

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
                const payload = buildPhotoPayload(summary?.results || [], resolvedPhotoType, msg.photoLabel);
                msg.payload = resultCount <= 1 ? payload.single : payload.multiple;

                const statusText = resultCount === 1
                    ? path.basename(payload.single?.PhotoPath || summary.results[0].output)
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
        pattern = DEFAULT_FILENAME_TEMPLATE;
    }

    pattern = pattern.replace(/\{\{\s*meterPosition\s*\}\}/gi, "{{meter}}");

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
    const list = parseMeterIndexes(rawIndexes);
    if (list.length > 0) {
        return list;
    }
    return [fallback];
}

function parseMeterIndexes(value) {
    if (Array.isArray(value)) {
        return uniqueMeterIndexes(value.map(Number));
    }

    if (typeof value === "string") {
        const tokens = value.split(/[\s,]+/).filter(Boolean);
        return uniqueMeterIndexes(tokens.map(Number));
    }

    if (Number.isFinite(value)) {
        const num = Number(value);
        return num > 0 ? [num] : [];
    }

    return [];
}

function uniqueMeterIndexes(numbers) {
    return Array.from(new Set(numbers.filter((n) => Number.isFinite(n) && n > 0)));
}

function buildCaptureArgs({ outputTemplate, bridgeFormat, zoneArgs, meterIndexes, ckDllPath }) {
    const args = ["--capture", outputTemplate];

    if (bridgeFormat && bridgeFormat !== "gray8") {
        args.push("--format", bridgeFormat);
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

function ensureExtension(outputPath, format) {
    if (!format || (format !== "jpg" && format !== "bmp")) {
        return outputPath;
    }

    const desiredExt = `.${format}`;
    if (outputPath.toLowerCase().endsWith(desiredExt)) {
        return outputPath;
    }

    const hasExtension = /\.[^\\/]+$/.test(outputPath);
    if (hasExtension) {
        return outputPath.replace(/\.[^\\/.]+$/, desiredExt);
    }

    return `${outputPath}${desiredExt}`;
}

function resolveFormats(requested) {
    if (requested === "jpeg") {
        requested = "jpg";
    }

    if (requested === "jpg") {
        return { outputFormat: "jpg", bridgeFormat: "gray8" };
    }

    const supported = new Set(["bmp", "gray8", "bgr24", "rgb24"]);
    if (!supported.has(requested)) {
        return { outputFormat: "bmp", bridgeFormat: "gray8" };
    }

    const bridgeFormat = requested === "bmp" ? "gray8" : requested;
    const outputFormat = requested === "bmp" ? "bmp" : requested;
    return { outputFormat, bridgeFormat };
}

function resolvePhotoType(msg) {
    if (msg.photoType != null && Number.isFinite(Number(msg.photoType))) {
        return Number(msg.photoType);
    }
    if (msg.picType != null && Number.isFinite(Number(msg.picType))) {
        return Number(msg.picType);
    }
    return 1;
}

function resolveBarCode(msg) {
    if (msg.barCode) {
        return msg.barCode;
    }
    if (msg.BarCode) {
        return msg.BarCode;
    }
    if (msg.barcode) {
        return msg.barcode;
    }
    return "";
}

function buildPhotoPayload(results, photoType, photoLabel) {
    const normalize = (result) => {
        if (!result || result.saved === false) {
            return null;
        }
        return {
            DataType: 0,
            MeterPosition: result.meterIndex,
            PhotoPath: result.output,
            PhotoType: photoType,
            PhotoLabel: photoLabel ?? undefined
        };
    };

    if (results.length <= 1) {
        const single = normalize(results[0]);
        return {
            single,
            multiple: single ? [single] : []
        };
    }

    return {
        single: null,
        multiple: results.map((result) => normalize(result)).filter(Boolean)
    };
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
