const { execFile } = require("child_process");
const fs = require("fs");
const path = require("path");

const DEFAULT_BRIDGE_PATH = path.resolve(__dirname, "bin", "CameraBridge.exe");
const DEFAULT_CK_DLL_PATH = path.resolve(__dirname, "bin", "CKGenCapture.dll");
const DEFAULT_OUTPUT_DIR = "D:\\\\Picture";
const DEFAULT_FILENAME_TEMPLATE = "{{barCode}}_{{meter}}_{{photoType}}.jpg";
const DEFAULT_FORMAT = "jpg";
const DEFAULT_OUTPUT_MODE = "path";
const DEFAULT_GEN_INI_PATH = path.resolve(__dirname, "bin", "GenCapture.ini");

module.exports = function (RED) {
    "use strict";

    function ZcameraConfigNode(config) {
        RED.nodes.createNode(this, config);
        this.iniPath = (config.iniPath || "").toString().trim();
    }

    RED.nodes.registerType("zcamera-config", ZcameraConfigNode);

    function ZcameraNode(config) {
        RED.nodes.createNode(this, config);
        const node = this;

        const configNode = config.configNode ? RED.nodes.getNode(config.configNode) : null;
        const baseOutputDir = config.outputDir || DEFAULT_OUTPUT_DIR;
        const baseFilename = config.defaultFilename || DEFAULT_FILENAME_TEMPLATE;
        const baseFormat = config.format || DEFAULT_FORMAT;
        const baseIniPath = configNode?.iniPath || "";
        const baseMeterIndex = Number(config.meterIndex) > 0 ? Number(config.meterIndex) : 1;
        const baseConfiguredMeterIndexes = parseMeterIndexes(config.meterIndexes);
        const baseOutputMode = normalizeOutputMode(config.outputMode) || DEFAULT_OUTPUT_MODE;

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
                const candidateCkDllPath = (msg.ckDllPath || DEFAULT_CK_DLL_PATH || "").toString().trim();
                const candidateIniPath = (msg.iniPath || msg.genCaptureIni || msg.configIni || baseIniPath || DEFAULT_GEN_INI_PATH || "").toString().trim();

                // Consolidate 表位/条码配置，支持 msg.barCodeMap
                const { meterIndexes, primaryMeterIndex, barCodeMap, firstBarCode } = resolveMeterInputs({
                    msg,
                    fallbackMeterIndex: meterIndex,
                    fallbackIndexes: baseConfiguredMeterIndexes
                });
                const resolvedPhotoType = resolvePhotoType(msg);
                const resolvedBarCode = firstBarCode ?? resolveBarCode(msg, primaryMeterIndex, barCodeMap);

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
                const outputMode = resolveOutputMode(baseOutputMode, msg);

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

                try {
                    ensureGenCaptureIni({
                        candidateIniPath,
                        resolvedCkDllPath,
                        logger: node
                    });
                } catch (iniError) {
                    node.status({ fill: "red", shape: "ring", text: iniError.message });
                    node.error(iniError, msg);
                    done(iniError);
                    return;
                }

                const captureArgs = buildCaptureArgs({
                    outputTemplate,
                    bridgeFormat,
                    zoneArgs,
                    meterIndexes,
                    ckDllPath: resolvedCkDllPath
                });

                msg.format = outputFormat;
                if (resolvedBarCode) {
                    msg.barCode = resolvedBarCode;
                }
                msg.photoType = resolvedPhotoType;
                msg.meterPosition = primaryMeterIndex;
                msg.meterIndexes = meterIndexes;
                msg.barCodeMap = barCodeMap;
                msg.outputMode = outputMode;

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
                const processedResults = processCaptureResults(summary?.results || [], {
                    photoType: resolvedPhotoType,
                    barCodeMap,
                    warn: (message) => node.warn(message)
                });

                const payload = buildPhotoPayload(processedResults, {
                    photoType: resolvedPhotoType,
                    photoLabel: msg.photoLabel,
                    outputMode,
                    barCodeMap
                });
                msg.dataType = payload.dataType;
                msg.payload = resultCount <= 1 ? payload.single : payload.multiple;

                const statusText = resultCount === 1
                    ? path.basename(payload.single?.PhotoPath || processedResults[0]?.output)
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

function resolveMeterInputs({ msg, fallbackMeterIndex, fallbackIndexes }) {
    // 汇总节点配置与消息中的条码/表位信息，生成唯一的 meter 索引数组
    let barCodeMap = {};
    let firstBarCode = null;
    const collectedIndexes = [];

    if (isPlainObject(msg?.barCodeMap)) {
        const normalized = normalizeBarCodeMapInput(msg.barCodeMap);
        barCodeMap = normalized.map;
        collectedIndexes.push(...normalized.meterIndexes);
        if (normalized.firstBarCode) {
            firstBarCode = normalized.firstBarCode;
        }
    }

    if (msg.meterIndexes !== undefined) {
        const parsedMsgIndexes = parseMeterIndexes(msg.meterIndexes);
        if (parsedMsgIndexes.length > 0) {
            collectedIndexes.push(...parsedMsgIndexes);
        }
    }

    if (collectedIndexes.length === 0 && Array.isArray(fallbackIndexes) && fallbackIndexes.length > 0) {
        collectedIndexes.push(...fallbackIndexes);
    }

    const combinedIndexes = collectedIndexes.length > 0 ? collectedIndexes : undefined;
    let meterIndexes = buildMeterIndexList(combinedIndexes, fallbackMeterIndex);
    if (!Array.isArray(meterIndexes) || meterIndexes.length === 0) {
        meterIndexes = [fallbackMeterIndex];
    }

    const primaryMeterIndex = meterIndexes[0] || fallbackMeterIndex;

    if (firstBarCode === null && barCodeMap[primaryMeterIndex]) {
        firstBarCode = barCodeMap[primaryMeterIndex];
    }

    return {
        meterIndexes,
        primaryMeterIndex,
        barCodeMap,
        firstBarCode
    };
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

function normalizeBarCodeMapInput(input) {
    const map = {};
    const meterIndexes = [];
    let firstBarCode = null;

    for (const [key, value] of Object.entries(input)) {
        let meterIndex = NaN;
        let barCode = null;

        if (value && typeof value === "object") {
            meterIndex = extractMeterIndex(value);
            barCode = resolveBarCodeFromEntry(value);
        }

        if (!Number.isFinite(meterIndex) || meterIndex <= 0) {
            const numericKey = Number(key);
            if (Number.isFinite(numericKey) && numericKey > 0) {
                meterIndex = numericKey;
            }
        }

        if (!barCode && typeof key === "string" && key.trim().length > 0) {
            barCode = key.trim();
        }

        if (Number.isFinite(meterIndex) && meterIndex > 0 && barCode) {
            if (!map[meterIndex]) {
                map[meterIndex] = barCode;
                meterIndexes.push(meterIndex);
                if (firstBarCode === null) {
                    firstBarCode = barCode;
                }
            }
        }
    }

    return { map, meterIndexes, firstBarCode };
}

function extractMeterIndex(entry) {
    if (!entry || typeof entry !== "object") {
        return NaN;
    }

    const candidates = [
        entry.meterPosition,
        entry.meterIndex,
        entry.meterNo,
        entry.MeterPosition,
        entry.MeterIndex,
        entry.MeterNo,
        entry.position,
        entry.index,
        entry.meter
    ];

    for (const candidate of candidates) {
        const num = Number(candidate);
        if (Number.isFinite(num) && num > 0) {
            return num;
        }
    }

    return NaN;
}

function resolveBarCodeFromEntry(entry) {
    if (!entry || typeof entry !== "object") {
        return null;
    }

    const candidates = [entry.barCode, entry.BarCode, entry.barcode, entry.code];
    for (const candidate of candidates) {
        if (typeof candidate === "string" && candidate.trim().length > 0) {
            return candidate.trim();
        }
    }

    return null;
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

function resolveBarCode(msg, meterIndex, barCodeMap) {
    if (barCodeMap && barCodeMap[meterIndex]) {
        return barCodeMap[meterIndex];
    }
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

function buildPhotoPayload(results, { photoType, photoLabel, outputMode, barCodeMap }) {
    const dataType = outputMode === "bytes" ? 1 : 2;

    const normalize = (result) => {
        if (!result || result.saved === false) {
            return null;
        }

        const meterIndex = result.meterIndex;
        const entry = {
            DataType: dataType,
            MeterPosition: meterIndex,
            PhotoType: photoType,
            PhotoLabel: photoLabel ?? undefined,
            PhotoPath: result.output
        };
        // 当调用方提供条码映射时，为每个表位写回 BarCode
        if (barCodeMap && barCodeMap[meterIndex]) {
            entry.BarCode = barCodeMap[meterIndex];
        }

        if (dataType === 1) {
            if (!result.output) {
                throw new Error("Bridge result missing file path while outputMode is bytes");
            }

            entry.PhotoContent = fs.readFileSync(result.output);
        }

        return entry;
    };

    if (results.length <= 1) {
        const single = normalize(results[0]);
        return {
            single,
            multiple: single ? [single] : [],
            dataType
        };
    }

    const multiple = results.map((result) => normalize(result)).filter(Boolean);
    return {
        single: null,
        multiple,
        dataType
    };
}

function normalizeOutputMode(value) {
    if (value === undefined || value === null) {
        return null;
    }

    if (typeof value === "number") {
        if (value === 1) {
            return "bytes";
        }
        if (value === 2) {
            return "path";
        }
    }

    const stringValue = String(value).trim().toLowerCase();
    if (stringValue === "bytes" || stringValue === "stream" || stringValue === "buffer") {
        return "bytes";
    }
    if (stringValue === "path" || stringValue === "file" || stringValue === "filename") {
        return "path";
    }
    if (stringValue === "1") {
        return "bytes";
    }
    if (stringValue === "2") {
        return "path";
    }
    return null;
}

function resolveOutputMode(baseMode, msg) {
    const fromDataType = normalizeOutputMode(msg?.dataType);
    if (fromDataType) {
        return fromDataType;
    }

    const fromMsg = normalizeOutputMode(msg?.outputMode ?? msg?.outputType);
    if (fromMsg) {
        return fromMsg;
    }

    return baseMode || DEFAULT_OUTPUT_MODE;
}

function isPlainObject(value) {
    return value !== null && typeof value === "object" && !Array.isArray(value);
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

function resolveIniSourcePath(candidatePath) {
    const attempts = [];

    if (candidatePath) {
        attempts.push(candidatePath);
        if (!path.isAbsolute(candidatePath)) {
            attempts.push(path.resolve(process.cwd(), candidatePath));
            attempts.push(path.resolve(__dirname, candidatePath));
        }
    }

    attempts.push(DEFAULT_GEN_INI_PATH);

    for (const attempt of attempts) {
        if (!attempt) {
            continue;
        }
        try {
            const stats = fs.statSync(attempt);
            if (stats.isFile()) {
                return attempt;
            }
            if (stats.isDirectory()) {
                const nested = path.join(attempt, "GenCapture.ini");
                if (fs.existsSync(nested) && fs.statSync(nested).isFile()) {
                    return nested;
                }
            }
        } catch (err) {
            // ignore and continue
        }
    }

    throw new Error("Unable to locate GenCapture.ini; please check configuration");
}

function ensureGenCaptureIni({ candidateIniPath, resolvedCkDllPath, logger }) {
    const targetDllPath = resolvedCkDllPath || DEFAULT_CK_DLL_PATH;
    const targetDir = path.dirname(targetDllPath);
    if (!fs.existsSync(targetDir)) {
        fs.mkdirSync(targetDir, { recursive: true });
    }

    const targetIniPath = path.join(targetDir, "GenCapture.ini");
    const sourceIniPath = resolveIniSourcePath(candidateIniPath);

    if (path.normalize(sourceIniPath) === path.normalize(targetIniPath)) {
        return;
    }

    try {
        if (logger?.log) {
            logger.log(`zcamera: using GenCapture.ini from ${sourceIniPath}`);
        }
        fs.copyFileSync(sourceIniPath, targetIniPath);
    } catch (error) {
        if (logger?.warn) {
            logger.warn(`zcamera: failed to copy GenCapture.ini (${error.message})`);
        }
        throw new Error(`Unable to copy GenCapture.ini to ${targetDir}`);
    }
}

function processCaptureResults(results, { photoType, barCodeMap, warn }) {
    if (!Array.isArray(results)) {
        return [];
    }

    return results.map((result) => {
        if (!result || result.saved === false || !result.output) {
            return result;
        }

        const meterIndex = result.meterIndex;
        const barCode = barCodeMap?.[meterIndex];
        if (!barCode) {
            return result;
        }

        try {
            const originalPath = result.output;
            const directory = path.dirname(originalPath);
            const extension = path.extname(originalPath);
            const desiredName = `${barCode}_${meterIndex}_${photoType}${extension}`;
            const desiredPath = path.join(directory, desiredName);

            if (path.normalize(desiredPath) === path.normalize(originalPath)) {
                return result;
            }

            if (fs.existsSync(desiredPath)) {
                fs.unlinkSync(desiredPath);
            }

            fs.renameSync(originalPath, desiredPath);
            return {
                ...result,
                output: desiredPath
            };
        } catch (error) {
            if (typeof warn === "function") {
                warn(`Failed to rename photo for meter ${meterIndex}: ${error.message}`);
            }
            return result;
        }
    });
}
