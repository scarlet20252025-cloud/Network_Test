import { UserStore } from './tools/proxy-users.mjs';
const s = new UserStore(process.argv[2]);
const u = s.add('新用户' + Math.floor(Math.random()*100));
console.log('已加:', u.name);
s.update(u.name, { note: '备注测试' });
