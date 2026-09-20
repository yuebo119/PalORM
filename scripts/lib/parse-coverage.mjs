// M3-1 覆盖率 XML 解析(dotnet-coverage results 格式)
// 用法: node parse-coverage.mjs <xml路径> <模块名片段> <地板json> <键>
import { readFileSync } from 'node:fs';
const [, , xmlPath, moduleSub, floorFile, key] = process.argv;
const xml = readFileSync(xmlPath, 'utf8');
const re = /<module [^>]*name="([^"]+)"[^>]*line_coverage="([0-9.]+)"/g;
let m, found = null;
while ((m = re.exec(xml)) !== null)
    if (m[1].includes(moduleSub)) { found = parseFloat(m[2]); break; }
if (found === null) { console.error(`::error::XML 中未找到模块 ${moduleSub}`); process.exit(1); }
const floors = JSON.parse(readFileSync(floorFile, 'utf8')).coverage;
const floor = floors[key];
if (floor === undefined) { console.error(`::error::地板文件缺 coverage.${key}`); process.exit(1); }
console.log(`${key}: line_coverage=${found}% (地板 ${floor}%)`);
if (found < floor) { console.error(`::error::覆盖率 ${found}% < 地板 ${floor}%`); process.exit(1); }
