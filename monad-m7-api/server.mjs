// server.mjs
import 'dotenv/config';
import express from 'express';
import cors from 'cors';
import helmet from 'helmet';
import { ethers } from 'ethers';
import crypto from 'node:crypto';

/* ---------------- Env & Tunables ---------------- */
// Core
const {
  RPC_URL,
  PRIVATE_KEY,
  CONTRACT_ADDRESS,
} = process.env;

// Server & logging
const PORT                 = Number(process.env.PORT || 3000);
const NODE_ENV             = String(process.env.NODE_ENV || 'production');
const DEBUG                = String(process.env.DEBUG || 'false').toLowerCase() === 'true';

// Anti-cheat
const WINDOW_MS            = Number(process.env.SCORE_WINDOW_MS || 60_000);   // 60s
const LIMIT_PER_WINDOW     = Number(process.env.SCORE_PER_MIN_LIMIT || 10_000);
const EVENT_MIN            = Number(process.env.MIN_SCORE_EVENT || 0);
const EVENT_MAX            = Number(process.env.MAX_SCORE_EVENT || 100);

// Tx / confirmations / timeouts
const CONFIRMATIONS        = Number(process.env.TX_CONFIRMATIONS || 1);
const TX_TIMEOUT_MS        = Number(process.env.TX_TIMEOUT_MS || 120_000);

// Batching/ack
const BATCH_INTERVAL_MS    = Number(process.env.BATCH_INTERVAL_MS || 5_000); // run every 5s
const RESPOND_AFTER_MS     = Number(process.env.RESPOND_AFTER_MS || 5_000);  // per-tx 5s immediate-return
const REQUEST_HARD_TIMEOUT = Number(process.env.REQUEST_HARD_TIMEOUT_MS || (BATCH_INTERVAL_MS + RESPOND_AFTER_MS + 5_000)); // fail-safe

// Leaderboard (unchanged, optional)
const LEADERBOARD_BASE     = process.env.LEADERBOARD_BASE || 'https://monad-games-id-site.vercel.app/leaderboard';
const LEADERBOARD_CACHE_MS = Number(process.env.LEADERBOARD_CACHE_MS || 15_000);

/* ---------------- Helpers ---------------- */
const log  = (...a) => console.log(new Date().toISOString(), ...a);
const dlog = (...a) => DEBUG && log('[DEBUG]', ...a);

if (!RPC_URL || !PRIVATE_KEY || !CONTRACT_ADDRESS) {
  console.error('Missing env vars. Please set RPC_URL, PRIVATE_KEY, CONTRACT_ADDRESS.');
  process.exit(1);
}

/* ---------------- Express ---------------- */
const app = express();
app.set('trust proxy', true);
app.use(helmet());
app.use(cors({ origin: true }));
app.use(express.json({ limit: '1mb' }));

// Request logger
app.use((req, res, next) => {
  const reqId = crypto.randomUUID();
  req.id = reqId;
  log(`[REQ ${reqId}] ${req.method} ${req.originalUrl} ip=${req.ip}`);
  dlog(`[REQ ${reqId}] headers=`, {
    'user-agent': req.get('user-agent'),
    origin: req.get('origin'),
    referer: req.get('referer'),
    'content-type': req.get('content-type'),
  });
  const t0 = Date.now();
  res.on('finish', () => log(`[RES ${reqId}] ${res.statusCode} ${req.method} ${req.originalUrl} (${Date.now()-t0}ms)`));
  next();
});

/* ---------------- Ethers Setup ---------------- */
const provider = new ethers.JsonRpcProvider(RPC_URL);
const wallet   = new ethers.Wallet(PRIVATE_KEY, provider);
const ABI = [
  {
    inputs: [
      { internalType: 'address', name: 'player', type: 'address' },
      { internalType: 'uint256', name: 'scoreAmount', type: 'uint256' },
      { internalType: 'uint256', name: 'transactionAmount', type: 'uint256' }
    ],
    name: 'updatePlayerData',
    outputs: [],
    stateMutability: 'nonpayable',
    type: 'function'
  },
  { inputs: [], name: 'GAME_ROLE', outputs: [{ internalType: 'bytes32', name: '', type: 'bytes32' }], stateMutability: 'view', type: 'function' },
  {
    inputs: [
      { internalType: 'bytes32', name: 'role', type: 'bytes32' },
      { internalType: 'address', name: 'account', type: 'address' }
    ],
    name: 'hasRole',
    outputs: [{ internalType: 'bool', name: '', type: 'bool' }],
    stateMutability: 'view',
    type: 'function'
  }
];
const contract = new ethers.Contract(CONTRACT_ADDRESS, ABI, wallet);
const iface    = new ethers.Interface(ABI);

// Boot logs
(async () => {
  try {
    const net = await provider.getNetwork();
    log(`[BOOT] chainId=${typeof net.chainId === 'bigint' ? Number(net.chainId) : net.chainId} signer=${wallet.address}`);
  } catch (e) {
    log('[BOOT] network query failed:', e?.shortMessage || e?.message || e);
  }
  try {
    const role = await contract.GAME_ROLE();
    const has  = await contract.hasRole(role, wallet.address);
    log(has
      ? `[BOOT] signer ${wallet.address} has GAME_ROLE`
      : `[BOOT] WARNING: signer ${wallet.address} lacks GAME_ROLE (txs may revert)`);
  } catch (e) {
    log('[BOOT] GAME_ROLE check failed:', e?.shortMessage || e?.message || e);
  }
})();

/* ---------------- Anti-cheat Sliding Window ---------------- */
const windows = new Map(); // addressLower -> { q: Array<{ts:number, score:number, jobId:string}>, sum:number }
function getWin(addrLower) {
  let w = windows.get(addrLower);
  if (!w) { w = { q: [], sum: 0 }; windows.set(addrLower, w); }
  return w;
}
function purgeOld(w, now) {
  while (w.q.length && (now - w.q[0].ts) > WINDOW_MS) {
    const e = w.q.shift();
    w.sum -= e.score;
  }
}
setInterval(() => {
  const now = Date.now();
  for (const [addr, w] of windows.entries()) {
    purgeOld(w, now);
    if (w.q.length === 0 && w.sum === 0) windows.delete(addr);
  }
}, Math.min(30_000, WINDOW_MS)).unref();

/* ---------------- Job Registry ---------------- */
/**
 * jobs[jobId] = {
 *   status: 'queued'|'sent'|'mined'|'failed',
 *   createdAt, sentAt?,
 *   walletAddress, score,
 *   nonce?, txHash?, receipt?,
 *   code?, reason?
 * }
 */
const jobs = new Map();
setInterval(() => {
  const cutoff = Date.now() - 15 * 60 * 1000;
  for (const [id, j] of jobs.entries()) {
    if ((j.createdAt || 0) < cutoff) jobs.delete(id);
  }
}, 60_000).unref();

/* ---------------- Submission Queue ---------------- */
/**
 * Each pending item:
 * {
 *   id, walletAddress, score, addrLower,
 *   res, responded: boolean,
 *   windowRef: { addrLower, score, ts } | null,
 *   reservationHeld?: boolean,
 *   skipWindow?: boolean,   // <<< privileged flag
 *   acceptedAt
 * }
 */
const pending = [];

/* ---------------- Utilities ---------------- */
function waitForReceiptWithTimeout(tx, confirmations, timeoutMs) {
  const waitP = tx.wait(confirmations);
  if (!timeoutMs || timeoutMs <= 0) return waitP;
  return Promise.race([
    waitP,
    new Promise((_, reject) => setTimeout(() => reject(new Error('TX_WAIT_TIMEOUT')), timeoutMs)),
  ]);
}

function sendOnce(item, fn) {
  if (!item || item.responded || item.res.headersSent) return false;
  item.responded = true;
  try { fn(); } catch { /* ignore */ }
  item.res = null; // release reference
  return true;
}

function rollbackWindow(jobId, addrLower, score) {
  try {
    const w = windows.get(addrLower);
    if (!w) return;
    for (let i = w.q.length - 1; i >= 0; i--) {
      const e = w.q[i];
      if (e.jobId === jobId || (e.score === score)) {
        w.sum -= e.score;
        w.q.splice(i, 1);
        break;
      }
    }
  } catch { /* ignore */ }
}

/* ---------------- Batch Dispatcher ---------------- */
async function processBatch() {
  if (pending.length === 0) return;

  // take the current batch
  const batch = pending.splice(0, pending.length);
  log(`[BATCH] processing ${batch.length} submissions`);

  // get base nonce once (pending includes unmined)
  let baseNonce;
  try {
    baseNonce = await provider.getTransactionCount(wallet.address, 'pending');
    dlog(`[BATCH] baseNonce=${baseNonce}`);
  } catch (e) {
    log('[BATCH] nonce fetch failed:', e?.shortMessage || e?.message || e);
    // Fail everyone in this batch
    for (const item of batch) {
      const reason = e?.shortMessage || e?.message || 'NONCE_FETCH_FAILED';
      const job = jobs.get(item.id) || {};
      jobs.set(item.id, { ...job, status: 'failed', code: 'NONCE_FETCH_FAILED', reason });
      // release any reservation they might hold
      rollbackWindow(item.id, item.addrLower, item.score);
      sendOnce(item, () => item.res.status(500).json({
        ok: false, error: 'Transaction failed', code: 'NONCE_FETCH_FAILED', reason
      }));
    }
    return;
  }

  // Fee data (EIP-1559 friendly)
  let feeData;
  try { feeData = await provider.getFeeData(); } catch { feeData = {}; }
  const maxFeePerGas         = feeData?.maxFeePerGas ?? feeData?.gasPrice ?? undefined;
  const maxPriorityFeePerGas = feeData?.maxPriorityFeePerGas ?? undefined;

  // Helper: ensure (or re-take) a reservation for this item right before sending.
  // If reservation would exceed the window cap, fail the item now.
  const ensureReservationOrDrop = (item) => {
    // NEW: privileged jobs bypass window admission entirely
    if (item?.skipWindow) return true;

    const now = Date.now();
    const w = getWin(item.addrLower);
    purgeOld(w, now);

    // If this item already holds a reservation entry, keep it.
    if (item.windowRef) return true;  // presence of windowRef = already reserved at intake

    // Recheck admission against current window
    const projected = w.sum + item.score;
    if (projected > LIMIT_PER_WINDOW) {
      const reason = `Score cap exceeded: ${w.sum}+${item.score} in the last ${Math.round(WINDOW_MS/1000)}s (limit ${LIMIT_PER_WINDOW}).`;
      jobs.set(item.id, {
        ...(jobs.get(item.id) || {}),
        status: 'failed',
        code: 'SUSPECTED_SCORE_HACKING',
        reason
      });
      sendOnce(item, () => item.res.status(403).json({
        ok: false,
        code: 'SUSPECTED_SCORE_HACKING',
        reason,
        window: { used: w.sum, incoming: item.score, limit: LIMIT_PER_WINDOW, seconds: Math.round(WINDOW_MS/1000) }
      }));
      return false;
    }

    // Take (or re-take) the reservation for this item
    const entry = { ts: now, score: item.score, jobId: item.id };
    w.q.push(entry);
    w.sum += item.score;
    item.windowRef = entry;
    item.reservationHeld = true;
    return true;
  };

  // We serialize SENDs to avoid nonce gaps, but wait receipts in parallel
  const receiptWaits = [];

  for (let i = 0; i < batch.length; i++) {
    const item  = batch[i];
    const nonce = baseNonce + i;

    try {
      // IMPORTANT: Recheck/take reservation if needed. If it fails, skip sending.
      if (!ensureReservationOrDrop(item)) continue;

      // --- estimate gas (ethers v6) with nonce override
      let gasEstimate;
      try {
        gasEstimate = await contract.estimateGas.updatePlayerData(
          item.walletAddress, BigInt(item.score), 1n, { nonce }
        );
      } catch {
        gasEstimate = 120_000n; // fallback
      }
      const gasLimit = (gasEstimate * 12n) / 10n + 5_000n; // +20% + small headroom

      // --- fee overrides
      const overrides = { nonce, gasLimit };
      if (maxFeePerGas)         overrides.maxFeePerGas = maxFeePerGas;
      if (maxPriorityFeePerGas) overrides.maxPriorityFeePerGas = maxPriorityFeePerGas;

      // mark job as sent (pre-send)
      const job0 = jobs.get(item.id) || {};
      jobs.set(item.id, { ...job0, status: 'sent', sentAt: Date.now(), nonce });

      // --- SEND (serialize: await this before moving to next nonce)
      const tx = await contract.updatePlayerData(
        item.walletAddress, BigInt(item.score), 1n, overrides
      );
      dlog(`[TX  ${item.id}] sent nonce=${nonce} hash=${tx.hash}`);

      // record tx hash
      const job1 = jobs.get(item.id) || {};
      jobs.set(item.id, { ...job1, txHash: tx.hash });

      // early-ack timer: 5s after SEND
      const ackTimer = setTimeout(() => {
        sendOnce(item, () => {
          item.res.set('X-Job-Id', item.id);
          item.res.status(202).json({
            ok: true,
            queued: true,
            message: `Transaction is processing. Poll /api/v1/jobs/${item.id} for status.`,
            jobId: item.id,
            statusUrl: `/api/v1/jobs/${item.id}`,
            nonce,
            ackMs: RESPOND_AFTER_MS
          });
        });
      }, RESPOND_AFTER_MS);

      // Wait for receipt in the background (do NOT block next send)
      const waiter = (async () => {
        let receipt;
        try {
          receipt = await waitForReceiptWithTimeout(tx, CONFIRMATIONS, TX_TIMEOUT_MS);
        } finally {
          clearTimeout(ackTimer);
        }

        dlog(`[RCPT ${item.id}] status=${receipt.status} block=${receipt.blockNumber}`);

        // job -> mined
        const job2 = jobs.get(item.id) || {};
        jobs.set(item.id, { ...job2, status: 'mined', receipt });

        // if client still waiting, reply 200 now
        sendOnce(item, () => item.res.json({
          ok: true,
          txHash: tx.hash,
          blockNumber: receipt.blockNumber,
          status: receipt.status,
          gasUsed: receipt.gasUsed?.toString?.(),
          to: receipt.to,
          from: receipt.from,
          nonce
        }));
      })().catch(err => {
        const code   = err?.code || err?.info?.error?.code;
        const reason = err?.shortMessage || err?.reason || err?.info?.error?.message || err?.message;
        log(`[ERR ${item.id}]`, code || '', reason || err);

        // job -> failed
        const job = jobs.get(item.id) || {};
        jobs.set(item.id, { ...job, status: 'failed', code, reason });

        // rollback this item's reservation (we took/held it)
        rollbackWindow(item.id, item.addrLower, item.score);

        // reply error if client still waiting
        sendOnce(item, () => {
          const http = (reason === 'TX_WAIT_TIMEOUT') ? 504 : 500;
          item.res.status(http).json({ ok: false, error: 'Transaction failed', code, reason });
        });
      });

      receiptWaits.push(waiter);

    } catch (err) {
      // SEND for this nonce failed → stop and re-queue the rest, but RELEASE their reservations now.
      const code   = err?.code || err?.info?.error?.code;
      const reason = err?.shortMessage || err?.reason || err?.info?.error?.message || err?.message;
      log(`[SEND-ERR nonce=${nonce}]`, code || '', reason || err);

      // mark failed + rollback window for this item
      const job = jobs.get(item.id) || {};
      jobs.set(item.id, { ...job, status: 'failed', code, reason });
      rollbackWindow(item.id, item.addrLower, item.score);
      sendOnce(item, () => item.res.status(500).json({
        ok: false, error: 'Transaction failed', code, reason
      }));

      // Re-queue remaining items for NEXT batch **without** reservation (force re-check then)
      for (let j = i + 1; j < batch.length; j++) {
        const rem = batch[j];

        // release their reservation if they had one
        if (rem.reservationHeld && rem.windowRef) {
          rollbackWindow(rem.id, rem.addrLower, rem.score);
        }
        rem.reservationHeld = false;
        rem.windowRef = null;

        // set status back to queued
        const jrec = jobs.get(rem.id) || {};
        jobs.set(rem.id, { ...jrec, status: 'queued', sentAt: undefined, nonce: undefined });

        // push back to the front so next batch picks them up first
        pending.unshift(rem);
      }
      break; // stop sending higher nonces this round
    }
  }

  // optional: observe when background waits all settle
  Promise.allSettled(receiptWaits).then(() => dlog('[BATCH] all receipt waits settled'));
}

setInterval(processBatch, BATCH_INTERVAL_MS).unref();

/* ---------------- Routes ---------------- */
app.get('/health', async (_req, res) => {
  try {
    const [net, blockNum] = await Promise.all([provider.getNetwork(), provider.getBlockNumber()]);
    res.json({
      status: 'ok',
      network: { chainId: typeof net.chainId === 'bigint' ? Number(net.chainId) : net.chainId },
      blockNumber: blockNum,
      signer: wallet.address,
      queueDepth: pending.length,
      windowMs: WINDOW_MS,
      perMinuteLimit: LIMIT_PER_WINDOW,
      eventRange: [EVENT_MIN, EVENT_MAX],
      confirmations: CONFIRMATIONS,
      txTimeoutMs: TX_TIMEOUT_MS,
      batchIntervalMs: BATCH_INTERVAL_MS,
      respondAfterMs: RESPOND_AFTER_MS
    });
  } catch (e) {
    res.json({ status: 'degraded', error: e?.message || String(e) });
  }
});

/**
 * POST /api/v1/submitscore
 * Body: { walletAddress: string, score: number }
 * Behavior:
 *   - Validates + enforces anti-cheat (0..100 per tx, 10k per 60s per wallet).
 *   - Enqueues the submission for the next batch.
 *   - The HTTP connection stays open until:
 *       a) the tx mines within RESPOND_AFTER_MS of send (-> 200), or
 *       b) RESPOND_AFTER_MS elapses after send (-> 202 with jobId), or
 *       c) fail fast (-> 4xx/5xx)
 *   - Failsafe REQUEST_HARD_TIMEOUT will issue 202 if the batch hasn’t sent yet.
 */
app.post('/api/v1/submitscore', (req, res) => {
  const reqId = req.id;
  try {
    const { walletAddress, score } = req.body ?? {};

    // Validate inputs
    if (typeof walletAddress !== 'string') {
      return res.status(400).json({ error: '`walletAddress` must be a string' });
    }
    if (!ethers.isAddress(walletAddress)) {
      return res.status(400).json({ error: '`walletAddress` is not a valid EVM address' });
    }
    const parsedScore =
      typeof score === 'string' ? Number(score) :
      typeof score === 'number' ? score : NaN;
    if (!Number.isFinite(parsedScore) || !Number.isInteger(parsedScore) || parsedScore < 0) {
      return res.status(400).json({ error: '`score` must be a non-negative integer' });
    }

    // Per-event sanity (anti-cheat: 0..100)
    if (parsedScore < EVENT_MIN || parsedScore > EVENT_MAX) {
      log(`[CHEAT ${reqId}] per-event out of range addr=${walletAddress} score=${parsedScore} allowed=[${EVENT_MIN},${EVENT_MAX}]`);
      return res.status(403).json({
        ok: false,
        code: 'SUSPECTED_SCORE_HACKING',
        reason: `Per-event score must be between ${EVENT_MIN} and ${EVENT_MAX}.`
      });
    }

    // Sliding window admission
    const now       = Date.now();
    const addrLower = walletAddress.toLowerCase();
    const w         = getWin(addrLower);
    purgeOld(w, now);
    const projected = w.sum + parsedScore;
    if (projected > LIMIT_PER_WINDOW) {
      log(`[CHEAT ${reqId}] minute cap exceeded addr=${walletAddress} sum=${w.sum} + ${parsedScore} > limit=${LIMIT_PER_WINDOW}`);
      return res.status(403).json({
        ok: false,
        code: 'SUSPECTED_SCORE_HACKING',
        reason: `Score cap exceeded: ${w.sum}+${parsedScore} in the last ${Math.round(WINDOW_MS/1000)}s (limit ${LIMIT_PER_WINDOW}).`,
        window: { used: w.sum, incoming: parsedScore, limit: LIMIT_PER_WINDOW, seconds: Math.round(WINDOW_MS/1000) }
      });
    }

    // Create job
    const jobId = reqId; // unique per request already
    jobs.set(jobId, {
      status: 'queued',
      createdAt: Date.now(),
      walletAddress,
      score: parsedScore
    });

    // Tentatively reserve in the window (rollback on failure)
    const entry = { ts: now, score: parsedScore, jobId }; // add jobId so rollback is precise
    w.q.push(entry);
    w.sum += parsedScore;

    // Enqueue submission
    const submission = {
      id: jobId,
      walletAddress,
      score: parsedScore,
      addrLower,
      res,
      responded: false,
      windowRef: entry,           // this means "I ALREADY hold a slot"
      reservationHeld: true,      // explicit marker
      acceptedAt: now
    };
    pending.push(submission);
    dlog(`[QUEUE] +1 pending=${pending.length} id=${jobId}`);

    // Failsafe: If batch hasn't sent within REQUEST_HARD_TIMEOUT, send 202
    const failsafe = setTimeout(() => {
      sendOnce(submission, () => {
        res.set('X-Job-Id', jobId);
        res.status(202).json({
          ok: true,
          queued: true,
          message: `Queued for next batch. Poll /api/v1/jobs/${jobId} for status.`,
          jobId,
          statusUrl: `/api/v1/jobs/${jobId}`,
          approxBatchInMs: BATCH_INTERVAL_MS
        });
      });
    }, REQUEST_HARD_TIMEOUT);

    // If we do eventually reply (by success/error/202-after-send), clear the failsafe:
    const stopFailsafe = () => clearTimeout(failsafe);
    // Hook into res finish to ensure cleanup
    res.on('finish', stopFailsafe);
    res.on('close',  stopFailsafe);

  } catch (err) {
    const code   = err?.code || err?.info?.error?.code;
    const reason = err?.shortMessage || err?.reason || err?.info?.error?.message || err?.message;
    log(`[ERR ${reqId}]`, code || '', reason || err);
    return res.status(500).json({ ok: false, error: 'INTERNAL_ERROR', code, reason });
  }
});

// Job status
app.get('/api/v1/jobs/:id', (req, res) => {
  const job = jobs.get(req.params.id);
  if (!job) return res.status(404).json({ ok: false, error: 'JOB_NOT_FOUND' });

  if (job.status === 'mined') {
    return res.json({
      ok: true,
      status: job.status,
      jobId: req.params.id,
      txHash: job.txHash,
      blockNumber: job.receipt?.blockNumber,
      statusCode: job.receipt?.status,
      gasUsed: job.receipt?.gasUsed?.toString?.(),
      to: job.receipt?.to,
      from: job.receipt?.from,
      nonce: job.nonce
    });
  }

  if (job.status === 'failed') {
    return res.json({
      ok: false,
      status: job.status,
      jobId: req.params.id,
      code: job.code,
      reason: job.reason
    });
  }

  // queued | sent
  return res.json({
    ok: true,
    status: job.status,
    jobId: req.params.id,
    sentAt: job.sentAt ?? null
  });
});

/* ---------------- (Optional) Leaderboard unchanged ---------------- */
let _fetch = globalThis.fetch;
if (!_fetch) {
  const mod = await import('node-fetch'); // npm i node-fetch
  _fetch = mod.default;
}

const MAX_PAGE_WALK = 50; // hard cap so we never hammer upstream
let lbCache = new Map();

/**
 * Parse the Next.js streamed HTML for the leaderboard JSON payload,
 * then normalize for a single page (scores + transactions).
 */
function extractLeaderboardFromHtml(html, wantedGameId, reqId = '') {
  const rePush = /self\.__next_f\.push\(\[1,"((?:\\.|[^"\\])*)"\]\)/g;
  let match;
  let pushes = 0;
  let hits = 0;

  while ((match = rePush.exec(html)) !== null) {
    pushes++;
    const captured = match[1];
    let decoded;
    try { decoded = JSON.parse(`"${captured}"`); } catch { continue; }
    const colon = decoded.indexOf(':');
    if (colon === -1) continue;

    const arrJson = decoded.slice(colon + 1);
    let arr;
    try { arr = JSON.parse(arrJson); } catch { continue; }
    if (!Array.isArray(arr) || arr.length < 4 || typeof arr[3] !== 'object' || arr[3] === null) continue;

    const payload = arr[3];

    const rootGameId = payload.gameId ?? null;
    const scoreHasWanted = Array.isArray(payload.scoreData) && payload.scoreData.some(x => Number(x.gameId) === Number(wantedGameId));
    const txHasWanted    = Array.isArray(payload.transactionData) && payload.transactionData.some(x => Number(x.gameId) === Number(wantedGameId));

    if (Number(rootGameId) === Number(wantedGameId) || scoreHasWanted || txHasWanted) {
      hits++;

      const normScore = (payload.scoreData || [])
        .filter(x => Number(x.gameId) === Number(wantedGameId))
        .map(({ userId, username, walletAddress, score, gameId, gameName, rank }) => ({
          userId, username, walletAddress, score,
          gameId: Number(gameId), gameName, rank: Number(rank)
        }))
        .sort((a, b) => a.rank - b.rank);

      const normTx = (payload.transactionData || [])
        .filter(x => Number(x.gameId) === Number(wantedGameId))
        .map(({ userId, username, walletAddress, transactionCount, gameId, gameName, rank }) => ({
          userId, username, walletAddress, transactionCount,
          gameId: Number(gameId), gameName, rank: Number(rank)
        }))
        .sort((a, b) => a.rank - b.rank);

      dlog?.(`[LB ${reqId}] pushes=${pushes} matched=${hits} (gameId=${wantedGameId})`);
      return {
        ok: true,
        gameId: Number(wantedGameId),
        gameName: normScore[0]?.gameName || normTx[0]?.gameName || payload.gameName || null,
        lastUpdated: payload.lastUpdated || null,
        scorePagination: payload.scorePagination || null,
        transactionPagination: payload.transactionPagination || null,
        scoreData: normScore,
        transactionData: normTx
      };
    }
  }

  dlog?.(`[LB ${reqId}] pushes scanned=${pushes}, no payload matched gameId=${wantedGameId}`);
  return { ok: false, error: 'PAYLOAD_NOT_FOUND_FOR_GAME' };
}

/** Fetch and parse ONE page from upstream. */
async function fetchLeaderboardPage({ baseUrl, gameId, page, reqId }) {
  const url = new URL(baseUrl);
  url.searchParams.set('gameId', String(gameId));
  url.searchParams.set('page', String(page));

  dlog?.(`[LB ${reqId}] fetching ${url.toString()}`);
  const resp = await _fetch(url.toString(), {
    method: 'GET',
    headers: {
      'User-Agent': 'score-middleware/1.0',
      'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8'
    }
  });

  const html = await resp.text();
  if (!resp.ok || !html) {
    throw new Error(`UPSTREAM_ERROR status=${resp.status}`);
  }

  const parsed = extractLeaderboardFromHtml(html, gameId, reqId);
  if (!parsed.ok) {
    throw new Error(parsed.error || 'PARSE_ERROR');
  }

  return { parsed, url: url.toString() };
}

/** Merge + de-dupe by userId+walletAddress (defensive, pages *should* be unique already). */
function mergeResults(acc, pageData) {
  const key = (u) => `${u.userId}::${u.walletAddress}`;
  for (const row of pageData.scoreData || []) {
    const k = key(row);
    if (!acc.scoreSeen.has(k)) {
      acc.scoreSeen.add(k);
      acc.scoreData.push(row);
    }
  }
  for (const row of pageData.transactionData || []) {
    const k = key(row);
    if (!acc.txSeen.has(k)) {
      acc.txSeen.add(k);
      acc.transactionData.push(row);
    }
  }
}

/** Walk all pages and return a single aggregated payload. */
async function fetchAllLeaderboardPages({ baseUrl, gameId, reqId }) {
  const pagesFetched = [];
  const acc = {
    scoreData: [],
    transactionData: [],
    scoreSeen: new Set(),
    txSeen: new Set(),
    gameName: null,
    lastUpdated: null,
    scorePagination: null,
    transactionPagination: null
  };

  // Page 1: discover totalPages
  const first = await fetchLeaderboardPage({ baseUrl, gameId, page: 1, reqId });
  pagesFetched.push(first.url);

  acc.gameName = first.parsed.gameName || acc.gameName;
  acc.lastUpdated = first.parsed.lastUpdated || acc.lastUpdated;
  acc.scorePagination = first.parsed.scorePagination || acc.scorePagination;
  acc.transactionPagination = first.parsed.transactionPagination || acc.transactionPagination;

  mergeResults(acc, first.parsed);

  const tpScore = Number(acc.scorePagination?.totalPages ?? 1);
  const tpTx    = Number(acc.transactionPagination?.totalPages ?? 1);
  const totalPages = Math.max(1, tpScore, tpTx);
  dlog?.(`[LB ${reqId}] totalPages (max of score/tx) = ${totalPages}`);

  // Walk the rest
  const hardCap = Math.min(MAX_PAGE_WALK, totalPages);
  for (let page = 2; page <= hardCap; page++) {
    try {
      const { parsed, url } = await fetchLeaderboardPage({ baseUrl, gameId, page, reqId });
      pagesFetched.push(url);

      // If a page returns empty arrays (common for > totalPages), stop early.
      const empty = (!parsed.scoreData?.length) && (!parsed.transactionData?.length);
      if (empty) {
        dlog?.(`[LB ${reqId}] page ${page} returned no data, stopping.`);
        break;
      }

      mergeResults(acc, parsed);

      // Track the latest "lastUpdated" we see
      acc.lastUpdated = parsed.lastUpdated || acc.lastUpdated;
    } catch (e) {
      // If something goes wrong mid-walk, bail out but keep what we have from earlier pages.
      log?.(`[LB ${reqId}] stopping at page due to ${e?.message || e}`);
      break;
    }
  }

  // Final sort by rank (just to be sure)
  acc.scoreData.sort((a, b) => a.rank - b.rank);
  acc.transactionData.sort((a, b) => a.rank - b.rank);

  return {
    ok: true,
    gameId: Number(gameId),
    gameName: acc.gameName || null,
    lastUpdated: acc.lastUpdated || null,
    scorePagination: acc.scorePagination || null,
    transactionPagination: acc.transactionPagination || null,
    scoreData: acc.scoreData,
    transactionData: acc.transactionData,
    source: {
      base: baseUrl,
      pages: pagesFetched,
      fetchedAt: new Date().toISOString()
    }
  };
}

app.get('/api/v1/getleaderboard', async (req, res) => {
  const reqId = req.id;
  const gameId = Number(req.query.gameId || 64);

  try {
    const cached = lbCache.get(gameId);
    if (cached && (Date.now() - cached.ts) < LEADERBOARD_CACHE_MS) {
      dlog?.(`[LB ${reqId}] cache hit for gameId=${gameId}`);
      return res.json({ ...cached.data, cached: true, cacheMs: LEADERBOARD_CACHE_MS });
    }

    // Walk all pages and aggregate
    const result = await fetchAllLeaderboardPages({
      baseUrl: LEADERBOARD_BASE,
      gameId,
      reqId
    });

    if (!result.ok) {
      log?.(`[LB ${reqId}] aggregate failed`);
      return res.status(500).json({ ok: false, error: 'AGGREGATE_FAILED' });
    }

    lbCache.set(gameId, { ts: Date.now(), data: result });
    return res.json(result);

  } catch (e) {
    const reason = e?.message || String(e);
    log?.(`[ERR ${reqId}] leaderboard error: ${reason}`);
    return res.status(500).json({ ok: false, error: 'INTERNAL_ERROR', reason });
  }
});

/* ---------------- Unlock-all helpers ---------------- */

/** Check wallet has username via upstream API. */
async function checkWalletHasUsername(walletAddress) {
  try {
    const url = new URL('https://monad-games-id-site.vercel.app/api/check-wallet');
    url.searchParams.set('wallet', walletAddress);
    const r = await _fetch(url.toString(), { headers: { 'Accept': 'application/json' } });
    const j = await r.json().catch(() => ({}));
    if (!r.ok) {
      return { ok: false, reason: `CHECK_WALLET_HTTP_${r.status}`, raw: j };
    }
    const has = !!j?.hasUsername;
    return { ok: true, hasUsername: has, payload: j };
  } catch (e) {
    return { ok: false, reason: e?.message || 'CHECK_WALLET_ERROR' };
  }
}

/**
 * Returns the current score for a wallet (case-insensitive) for a given gameId.
 * Uses cache if fresh; otherwise walks pages via fetchAllLeaderboardPages.
 */
async function getCurrentScoreForWallet(gameId, walletLower, reqId = '') {
  // Try cache first
  const cached = lbCache.get(gameId);
  let data;
  if (cached && (Date.now() - cached.ts) < LEADERBOARD_CACHE_MS) {
    data = cached.data;
  } else {
    data = await fetchAllLeaderboardPages({
      baseUrl: LEADERBOARD_BASE,
      gameId,
      reqId
    });
    if (data?.ok) lbCache.set(gameId, { ts: Date.now(), data });
  }

  if (!data?.ok) throw new Error('LEADERBOARD_UNAVAILABLE');

  const row = (data.scoreData || []).find(
    r => String(r.walletAddress || '').toLowerCase() === walletLower
  );
  return { score: row?.score ? Number(row.score) : 0, gameName: data.gameName ?? null };
}

/**
 * POST /api/v1/s3cr3tUnlockAll
 * Body: { walletAddress: string, gameId?: number }
 *
 * Behavior:
 *   - Requires the wallet to have a username (via /api/check-wallet).
 *   - Reads current score from leaderboard and computes delta to reach 1200.
 *   - If already >= 1200, rejects.
 *   - Enqueues a privileged job that BYPASSES per-event limits and sliding-window.
 *   - Uses the same early-ack behavior as /submitscore.
 */
app.post('/api/v1/s3cr3tUnlockAll', async (req, res) => {
  const reqId = req.id;
  try {
    const { walletAddress, gameId: gameIdRaw } = req.body ?? {};
    const gameId = Number(gameIdRaw ?? req.query.gameId ?? 64);

    // Validate inputs
    if (typeof walletAddress !== 'string') {
      return res.status(400).json({ ok: false, error: '`walletAddress` must be a string' });
    }
    if (!ethers.isAddress(walletAddress)) {
      return res.status(400).json({ ok: false, error: '`walletAddress` is not a valid EVM address' });
    }
    if (!Number.isFinite(gameId) || gameId <= 0) {
      return res.status(400).json({ ok: false, error: '`gameId` must be a positive number' });
    }

    // Require the account to be "set" (has a username)
    const chk = await checkWalletHasUsername(walletAddress);
    if (!chk.ok) {
      return res.status(502).json({ ok: false, code: 'CHECK_WALLET_ERROR', reason: chk.reason });
    }
    if (!chk.hasUsername) {
      return res.status(403).json({
        ok: false,
        code: 'ACCOUNT_NOT_SET',
        reason: 'Wallet has no username set in Monad Games ID.'
      });
    }

    // If already 1200 or more, reject; otherwise compute delta
    const addrLower = walletAddress.toLowerCase();
    const { score: currentScore, gameName } = await getCurrentScoreForWallet(gameId, addrLower, reqId);
    const TARGET = 1200;

    if (currentScore >= TARGET) {
      return res.status(409).json({
        ok: false,
        code: 'ALREADY_MAXED',
        reason: `Player already at or above ${TARGET}.`,
        currentScore,
        gameId,
        gameName
      });
    }

    const delta = TARGET - currentScore; // how much we need to reach 1200
    if (delta <= 0) {
      return res.status(409).json({
        ok: false,
        code: 'NO_DELTA',
        reason: 'Nothing to do.',
        currentScore,
        gameId,
        gameName
      });
    }

    // Create a privileged job that skips anti-cheat/window checks entirely
    const jobId = reqId; // unique per request already
    jobs.set(jobId, {
      status: 'queued',
      createdAt: Date.now(),
      walletAddress,
      score: delta,
      unlockAll: true,
      note: 'privileged delta to reach 1200'
    });

    const submission = {
      id: jobId,
      walletAddress,
      score: delta,
      addrLower,
      res,
      responded: false,
      // IMPORTANT: no windowRef, no reservationHeld → won't consume the sliding window
      windowRef: null,
      reservationHeld: false,
      skipWindow: true,     // <<< this toggles the bypass in processBatch()
      acceptedAt: Date.now()
    };
    pending.push(submission);
    dlog?.(`[QUEUE] +1 (unlockAll) pending=${pending.length} id=${jobId} delta=${delta}`);

    // Failsafe: if batch hasn’t sent yet, 202 after REQUEST_HARD_TIMEOUT
    const failsafe = setTimeout(() => {
      sendOnce(submission, () => {
        res.set('X-Job-Id', jobId);
        res.set('X-Delta', String(delta));
        res.status(202).json({
          ok: true,
          queued: true,
          message: `Unlock queued. Poll /api/v1/jobs/${jobId} for status.`,
          jobId,
          statusUrl: `/api/v1/jobs/${jobId}`,
          delta,
          target: TARGET,
          currentScore,
          gameId,
          approxBatchInMs: BATCH_INTERVAL_MS
        });
      });
    }, REQUEST_HARD_TIMEOUT);

    const stopFailsafe = () => clearTimeout(failsafe);
    res.on('finish', stopFailsafe);
    res.on('close',  stopFailsafe);

  } catch (err) {
    const code   = err?.code || err?.info?.error?.code;
    const reason = err?.shortMessage || err?.reason || err?.info?.error?.message || err?.message;
    log(`[ERR ${reqId}] s3cr3tUnlockAll`, code || '', reason || err);
    return res.status(500).json({ ok: false, error: 'INTERNAL_ERROR', code, reason });
  }
});

/* ---------------- Start Server ---------------- */
app.listen(PORT, () => {
  log(`[score-middleware] Listening on http://localhost:${PORT} (${NODE_ENV}) debug=${DEBUG}`);
  log(`[anti-cheat] window=${Math.round(WINDOW_MS/1000)}s limit=${LIMIT_PER_WINDOW}/window event=[${EVENT_MIN},${EVENT_MAX}]`);
  log(`[batch] interval=${BATCH_INTERVAL_MS}ms respondAfter=${RESPOND_AFTER_MS}ms`);
});
