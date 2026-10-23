// 验证 users.json 的 UTF-8 写入是否真的正确（不依赖 PowerShell）
import { UserStore } from '../tools/proxy-users.mjs';
import fs from 'node:fs';

const f = process.argv[2];
const before = fs.existsSync(f) ? fs.readFileSync(f).toString('latin1') : '';

const s = new UserStore(f);
const nm = '用户' + Math.floor(Math.random() * 1000);
const u = s.add(nm);
console.log('已添加:', nm);

const buf = fs.readFileSync(f);
const txt = buf.toString('utf8');
console.log('磁盘字节里包含 "用户" UTF-8 (e794a8e688b7):', txt.includes('用户'));
console.log('磁盘字节里含 GBK 乱码 (娴嬭):', txt.includes('娴嬭'));
const parsed = JSON.parse(txt);
console.log('回读用户名:', parsed.users.map((x) => x.name).join(', '));
const ok = parsed.users.some((x) => x.name === nm);
console.log(ok ? '✅ PASS' : '❌ FAIL');
