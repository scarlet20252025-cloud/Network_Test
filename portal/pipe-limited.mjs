/**
 * 限速转发（队列式）—— 替代 pipeLimited 的 setTimeout 版本。
 *
 * 为什么第一版是错的（实测：5MB 丢 64KB、速率 4.93 而不是 2）：
 *   在 'data' 回调里 pause() 然后 setTimeout，看似正确，但 Node 在
 *   pause 生效前可能已经 emit 了多个已缓冲的 chunk，于是**多个 setTimeout
 *   并发排队**，顺序错乱；而且 src 'close' 可能先于最后几个 setTimeout
 *   触发，导致尾部 chunk 丢失。
 *
 * 正确做法：单泵 + 队列。'data' 只负责入队，泵负责按节奏出队。
 *   · 队列有界（高水位），超了就 pause src —— 这是真背压
 *   · 泵是串行的，不会并发 setTimeout
 *   · 只有队列空且 src 已 end 时才 dst.end()
 */
function makePipeLimited(takeTokens) {
  return function pipeLimited(src, dst, limiter) {
    const queue = [];
    let pumping = false;
    let srcEnded = false;
    let dstClosed = false;
    // 高水位：队列里堆到这么多字节就 pause，防内存膨胀。
    // 1MB 足够吸收网络抖动，又不会让内存失控。
    const HIGH_WATER = 1024 * 1024;
    let queued = 0;

    const finish = () => {
      if (dstClosed) return;
      dstClosed = true;
      if (dst.writable) dst.end();
    };

    const pump = () => {
      if (pumping) return;
      pumping = true;

      const step = () => {
        if (dstClosed) { pumping = false; return; }

        if (queue.length === 0) {
          pumping = false;
          if (srcEnded) finish();
          return;
        }

        const chunk = queue.shift();
        queued -= chunk.length;

        // 队列降下来了，恢复读上游
        if (queued < HIGH_WATER / 2) src.resume();

        const waitMs = limiter ? limiter(chunk.length) : 0;
        const write = () => {
          if (dstClosed) { pumping = false; return; }
          if (!dst.writable) { finish(); pumping = false; return; }
          const ok = dst.write(chunk);
          if (ok) step();
          else dst.once('drain', step);   // 等下游消化，不丢数据
        };

        if (waitMs > 0) setTimeout(write, waitMs);
        else write();
      };

      step();
    };

    src.on('data', (chunk) => {
      queue.push(chunk);
      queued += chunk.length;
      if (queued >= HIGH_WATER) src.pause();
      pump();
    });

    src.on('end', () => { srcEnded = true; pump(); });
    src.on('close', () => { srcEnded = true; pump(); });
    src.on('error', () => { if (dst.writable) dst.destroy(); });
    dst.on('error', () => src.destroy());
    dst.on('close', () => { dstClosed = true; });
  };
}

// 导出给测试用
export { makePipeLimited };

/**
 * GCRA（Generic Cell Rate Algorithm）限速器 —— 替代令牌桶。
 *
 * 为什么不用令牌桶（实测问题）：
 *   标准令牌桶初始给满一桶（= 1 秒配额），前 1 秒的流量完全免费。
 *   实测 5MB @ 2MB/s 只花了 1.03s（应该 2.5s），速率 4.86 MB/s —— 超了 2.4 倍。
 *   桶越大偏差越大，20MB @ 5MB/s 实测 8.08 MB/s。
 *
 * GCRA 的做法：只维护一个「下一个包最早可发时间」。
 *   每个包消耗 n/rate 秒，如果这个时间还没到就等。
 *   没有桶、没有突发、没有初始免费额度，速率天然准确。
 *
 * 唯一的容差是 TOLERANCE（允许的突发时间，默认 0.05s），
 * 给一点余量避免 TCP 被压得太死导致窗口塌陷。
 */
export function makeGcraLimiter() {
  const next = new Map();   // key -> 下一个包最早可发的时间（ms）
  const TOLERANCE_MS = 50;  // 允许 50ms 的突发

  return function take(key, n, rate) {
    if (!rate || rate <= 0) return 0;
    const nowMs = Date.now();
    const costMs = (n / rate) * 1000;
    let t = next.get(key);
    if (t === undefined) t = nowMs;

    // 允许最多 TOLERANCE_MS 的提前量
    const earliest = Math.max(nowMs - TOLERANCE_MS, t);
    next.set(key, earliest + costMs);

    const waitMs = earliest + costMs - costMs - nowMs;  // = earliest - nowMs
    return waitMs > 0 ? Math.ceil(waitMs) : 0;
  };
}
