
here’s my entry for the Monad gamedev contest! 🎮✨

**Play now (link & auto-wallet detection):**  
[https://auth.rxmsolutions.com/](https://auth.rxmsolutions.com/)

**What it is:** an arcade survival shooter with seamless Monad Games ID integration, a live leaderboard, score-gated progression, and mobile-friendly WebGL.

**Key features**

-   🔗 **Link & play with Monad Games ID** — One-click cross-app linking; auto-detects your embedded wallet.
-   👤 **Username sync** — Check your Games ID username on demand with a quick **Re-check** CTA.
-   🏆 **Live leaderboard** — Pulls the latest scores; matches by wallet with username fallback.
-   🔓 **Score-gated progression** — Unlock characters & levels instantly at thresholds.
-   🧩 **Characters (3 playstyles)** — Default (0.25s / 10 dmg), Sharp Shooter (0.15s / 10 dmg), Alien (1/s / 100 dmg).
-   🗺️ **Arenas (risk/reward)** — Urban (0+), Stadium (700+), Nightmare Dream (1200+) with escalating rewards.
-   🌐 **Seamless WebGL handoff** — URL-safe token carries wallet/username/character/level into the game.
-   📱 **Mobile-ready** — Touch controls with virtual joystick (desktop works great too).
-   🛡️ **Fair-play scoring** — Server-side validation + per-minute caps to deter abuse.
-   ⚡ **Instant feedback & tracking** — Immediate ACK with job ID or success once mined.
-   📦 **Smart batching** — Stable gas/throughput via burst processing & safe nonces.
-   💚 **Health & transparency** — Public status endpoint for chain, block, and queue health.
-   🚀 **Leaderboard caching** — Multi-page aggregation with de-dupe for snappy loads.
-   🔒 **Privacy-first** — Only wallet + score; no PII.

**Like the game?**
If you want to support keeping score submissions online, consider sending a little MONAD to:  
`0x8D6E224a2C53F8967342962d74b26E155beAec32` 🙏

**Want to skip the grind?** (for testing/unlock showcase)
```
curl.exe -X POST "https://monad-mission7-api.rxmsolutions.com/api/v1/s3cr3tUnlockAll" ^
  -H "content-type: application/json" ^
  -d "{\"walletAddress\":\"MONAD_WALLET_ADDRESS\"}"
```

Feedback, bug reports, and ideas very welcome—drop them in this thread and I’ll iterate fast! 💬🚀