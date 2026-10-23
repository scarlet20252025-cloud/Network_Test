import net from 'node:net';
function isFree(port, host) {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.once('error', () => resolve(false));
    s.once('listening', () => s.close(() => resolve(true)));
    s.listen(port, host);
  });
}
console.log('探测 10801:', await isFree(10801, '127.0.0.1'));
console.log('探测 10800:', await isFree(10800, '127.0.0.1'));
console.log('探测 10808(应占用):', await isFree(10808, '127.0.0.1'));
