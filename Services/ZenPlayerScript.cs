namespace ZenTube.Services;

public static class ZenPlayerScript
{
    public const string BootstrapScript = """
        (function() {
            if (window.__zentubeInitialized) return;
            window.__zentubeInitialized = true;

            // Prismil 独自プレイヤー画面そのものでは何もしない（画面側に専用の処理がある）
            const PRISMIL_HOST = 'player.prismil.local';
            if (location.hostname === PRISMIL_HOST) return;

            // 独自プレイヤーの中に埋め込まれた動画フレームかどうか
            const inPrismil = window !== window.top && (() => {
                try {
                    const a = location.ancestorOrigins;
                    if (a) { for (let i = 0; i < a.length; i++) if (a[i].indexOf(PRISMIL_HOST) >= 0) return true; }
                } catch {}
                return false;
            })();
            window.__zentubeZenMode = true;
            window.__zentubeAdBlock = true;
            window.__zentubeBlockShorts = true;
            window.__zentubeAmbilight = 'vivid'; // 'off' | 'soft' | 'vivid' | 'ultra'
            window.__zentubeDanmakuStyle = 'normal'; // 'off' | 'normal' | 'bold' | 'subtle'
            window.__zentubePlaybackRate = 1.0;
            window.__zentubeLoop = false;
            window.__zentubeMirror = false;
            window.__zentubeDim = false;
            window.__zentubeAudioGain = 1.0;
            window.__zentubeAudioPreset = 'normal';
            window.__zentubePointA = null;
            window.__zentubePointB = null;
            window.__zentubeTimedComments = [];
            window.__zentubeGeneralComments = [];
            window.__zentubeGeneralCursor = 0;
            let wasAdPlaying = false;
            let lastColorSend = 0;
            let lastDanmakuSpawn = 0;
            let lastVideoTime = -1;
            const laneBusyUntil = new Array(11).fill(0);

            const styleId = 'zentube-zen-style';
            const ambiId = 'zentube-ambilight-backdrop';
            const ambiCanvasId = 'zentube-ambilight-canvas';
            const danmakuLayerId = 'zentube-danmaku-layer';

            const css = `
                /* ================= 広告ブロック用CSS (常時有効可) ================= */
                html.zentube-adblock-active ytd-ad-slot-renderer,
                html.zentube-adblock-active ytd-in-feed-ad-layout-renderer,
                html.zentube-adblock-active ytd-banner-promo-renderer,
                html.zentube-adblock-active ytd-statement-banner-renderer,
                html.zentube-adblock-active ytd-promoted-sparkles-web-renderer,
                html.zentube-adblock-active ytd-promoted-video-renderer,
                html.zentube-adblock-active ytd-display-ad-renderer,
                html.zentube-adblock-active ytd-companion-slot-renderer,
                html.zentube-adblock-active #player-ads,
                html.zentube-adblock-active #masthead-ad,
                html.zentube-adblock-active .ytp-ad-overlay-container,
                html.zentube-adblock-active .ytp-ad-message-container,
                html.zentube-adblock-active .ytd-mealbar-promo-renderer,
                html.zentube-adblock-active tp-yt-paper-dialog:has(ytd-enforcement-message-view-model),
                html.zentube-adblock-active [class*="Advertisement"],
                html.zentube-adblock-active [class*="AdsBanner"],
                html.zentube-adblock-active .vcd,
                html.zentube-adblock-active .ad-report,
                html.zentube-adblock-active .bili-mini-mask,
                html.zentube-adblock-active .bili-login-card,
                html.zentube-adblock-active .login-panel-popover,
                html.zentube-adblock-active .vip-login-tip,
                html.zentube-adblock-active ytd-consent-bump-v2-lightbox,
                html.zentube-adblock-active tp-yt-iron-overlay-backdrop,
                html.zentube-adblock-active yt-upsell-dialog-renderer,
                html.zentube-adblock-active [class*="LoginPromotion"],
                html.zentube-adblock-active [class*="PremiumPromotion"] {
                    display: none !important;
                }

                /* ================= ショート動画 (Shorts) 完全非表示用CSS ================= */
                html.zentube-block-shorts ytd-reel-shelf-renderer,
                html.zentube-block-shorts ytd-rich-shelf-renderer[is-shorts],
                html.zentube-block-shorts ytd-shorts,
                html.zentube-block-shorts a[title="Shorts"],
                html.zentube-block-shorts a[title="ショート"],
                html.zentube-block-shorts ytd-mini-guide-entry-renderer[aria-label="Shorts"],
                html.zentube-block-shorts ytd-mini-guide-entry-renderer[aria-label="ショート"],
                html.zentube-block-shorts ytd-guide-entry-renderer:has(a[title="Shorts"]),
                html.zentube-block-shorts ytd-guide-entry-renderer:has(a[title="ショート"]),
                html.zentube-block-shorts ytd-video-renderer:has(a[href*="/shorts/"]),
                html.zentube-block-shorts ytd-grid-video-renderer:has(a[href*="/shorts/"]),
                html.zentube-block-shorts ytd-compact-video-renderer:has(a[href*="/shorts/"]),
                html.zentube-block-shorts ytd-rich-item-renderer:has(a[href*="/shorts/"]) {
                    display: none !important;
                }

                /* ================= 映像エフェクト（左右反転・夜間減光） ================= */
                html.zentube-mirror-active video {
                    transform: scaleX(-1) !important;
                }
                html.zentube-dim-active video {
                    filter: brightness(0.68) contrast(1.04) !important;
                }

                /* ================= 共通：Zenモード有効時の背景とスクロール抑止 ================= */
                html.zentube-zen-active,
                html.zentube-zen-active body {
                    overflow: hidden !important;
                    background: #030407 !important;
                    margin: 0 !important;
                    padding: 0 !important;
                }

                /* ================= アンビエントライト背景キャンバス ================= */
                #zentube-ambilight-backdrop {
                    display: none;
                    position: fixed !important;
                    inset: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    z-index: 2147483635 !important;
                    background: #040508 !important;
                    overflow: hidden !important;
                    pointer-events: none !important;
                }

                html.zentube-zen-active.zentube-ambilight-active #zentube-ambilight-backdrop {
                    display: block !important;
                }

                #zentube-ambilight-canvas {
                    position: absolute !important;
                    inset: -8% !important;
                    width: 116% !important;
                    height: 116% !important;
                    object-fit: cover !important;
                    filter: blur(54px) saturate(230%) brightness(1.18) !important;
                    opacity: 0.88 !important;
                    transform: scale(1.08) !important;
                    pointer-events: none !important;
                }

                html.zentube-ambilight-soft #zentube-ambilight-canvas {
                    filter: blur(64px) saturate(175%) brightness(0.92) !important;
                    opacity: 0.65 !important;
                }

                html.zentube-ambilight-ultra #zentube-ambilight-canvas {
                    filter: blur(46px) saturate(290%) brightness(1.38) !important;
                    opacity: 1.0 !important;
                }

                #zentube-ambilight-vignette {
                    position: absolute !important;
                    inset: 0 !important;
                    background: radial-gradient(circle at center, transparent 48%, rgba(4, 5, 9, 0.72) 100%) !important;
                    pointer-events: none !important;
                }

                /* ================= YouTube ================= */
                html.zentube-zen-active.zentube-site-yt #masthead-container,
                html.zentube-zen-active.zentube-site-yt ytd-masthead,
                html.zentube-zen-active.zentube-site-yt #secondary,
                html.zentube-zen-active.zentube-site-yt #below,
                html.zentube-zen-active.zentube-site-yt #related,
                html.zentube-zen-active.zentube-site-yt #comments,
                html.zentube-zen-active.zentube-site-yt tp-yt-app-drawer,
                html.zentube-zen-active.zentube-site-yt ytd-merch-shelf-renderer {
                    display: none !important;
                }

                /* ライブチャット欄は非表示にしつつDOMを更新させ続けてリアルタイム弾幕として抽出 */
                html.zentube-zen-active.zentube-site-yt #chat-container,
                html.zentube-zen-active.zentube-site-twitch .right-column,
                html.zentube-zen-active.zentube-site-twitch .channel-root__right-column {
                    position: fixed !important;
                    left: -9999px !important;
                    top: 0 !important;
                    width: 340px !important;
                    height: 600px !important;
                    opacity: 0 !important;
                    pointer-events: none !important;
                }

                html.zentube-zen-active.zentube-site-yt #movie_player {
                    position: fixed !important;
                    inset: 0 !important;
                    top: 0 !important;
                    left: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    max-width: 100vw !important;
                    max-height: 100vh !important;
                    z-index: 2147483640 !important;
                    background: #000000 !important;
                    transition: all 0.32s cubic-bezier(0.2, 0.8, 0.2, 1) !important;
                }

                html.zentube-zen-active.zentube-site-yt #movie_player .html5-video-container {
                    width: 100% !important;
                    height: 100% !important;
                }

                html.zentube-zen-active.zentube-site-yt #movie_player video.html5-main-video {
                    width: 100% !important;
                    height: 100% !important;
                    left: 0 !important;
                    top: 0 !important;
                    object-fit: contain !important;
                }

                html.zentube-zen-active.zentube-site-yt #movie_player .ytp-chrome-bottom {
                    width: calc(100% - 28px) !important;
                    left: 14px !important;
                }

                /* ================= bilibili ================= */
                html.zentube-zen-active.zentube-site-bili #biliMainHeader,
                html.zentube-zen-active.zentube-site-bili .bili-header,
                html.zentube-zen-active.zentube-site-bili .right-container,
                html.zentube-zen-active.zentube-site-bili .left-container-under-player,
                html.zentube-zen-active.zentube-site-bili #v_desc,
                html.zentube-zen-active.zentube-site-bili #commentapp,
                html.zentube-zen-active.zentube-site-bili .bili-mini-mask,
                html.zentube-zen-active.zentube-site-bili .login-panel-popover,
                html.zentube-zen-active.zentube-site-bili .login-tip,
                html.zentube-zen-active.zentube-site-bili .bpx-player-ending-related {
                    display: none !important;
                }

                /* プレイヤーの外側の枠（スクロール追従の sticky など）が重なり順を閉じ込めて、
                   プレイヤーが背景の光の後ろに隠れてしまうので、プレイヤー表示中は外す */
                html.zentube-zen-active.zentube-site-bili .left-container,
                html.zentube-zen-active.zentube-site-bili #playerWrap,
                html.zentube-zen-active.zentube-site-bili #mirror-vdcon,
                html.zentube-zen-active.zentube-site-bili .video-container-v1 {
                    position: static !important;
                    z-index: auto !important;
                    transform: none !important;
                    filter: none !important;
                    contain: none !important;
                }

                html.zentube-zen-active.zentube-site-bili #bilibili-player,
                html.zentube-zen-active.zentube-site-bili .bpx-player-container {
                    position: fixed !important;
                    inset: 0 !important;
                    top: 0 !important;
                    left: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    max-width: 100vw !important;
                    max-height: 100vh !important;
                    z-index: 2147483640 !important;
                    background: #000000 !important;
                    margin: 0 !important;
                    box-shadow: none !important;
                    transition: all 0.32s cubic-bezier(0.2, 0.8, 0.2, 1) !important;
                }

                /* ================= ニコニコ動画 ＆ ABEMA ================= */
                html.zentube-zen-active.zentube-site-nico header,
                html.zentube-zen-active.zentube-site-nico footer,
                html.zentube-zen-active.zentube-site-nico aside,
                html.zentube-zen-active.zentube-site-nico [class*="Header"],
                html.zentube-zen-active.zentube-site-nico [data-zentube-hide="1"],
                html.zentube-zen-active.zentube-site-abema header,
                html.zentube-zen-active.zentube-site-abema footer,
                html.zentube-zen-active.zentube-site-abema nav,
                html.zentube-zen-active.zentube-site-abema aside {
                    display: none !important;
                }

                html.zentube-zen-active.zentube-site-nico [data-zentube-nico-player="1"],
                html.zentube-zen-active.zentube-site-abema [data-zentube-nico-player="1"] {
                    position: fixed !important;
                    inset: 0 !important;
                    top: 0 !important;
                    left: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    max-width: 100vw !important;
                    max-height: 100vh !important;
                    z-index: 2147483640 !important;
                    background: #000000 !important;
                    margin: 0 !important;
                    padding: 0 !important;
                    transition: all 0.32s cubic-bezier(0.2, 0.8, 0.2, 1) !important;
                }

                /* ================= Twitch ================= */
                html.zentube-zen-active.zentube-site-twitch .top-nav,
                html.zentube-zen-active.zentube-site-twitch .side-nav,
                html.zentube-zen-active.zentube-site-twitch .channel-info-content,
                html.zentube-zen-active.zentube-site-twitch #channel-player-disclosures,
                html.zentube-zen-active.zentube-site-twitch [data-a-target="consent-banner"] {
                    display: none !important;
                }

                html.zentube-zen-active.zentube-site-twitch .persistent-player,
                html.zentube-zen-active.zentube-site-twitch .video-player__container {
                    position: fixed !important;
                    inset: 0 !important;
                    top: 0 !important;
                    left: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    max-width: 100vw !important;
                    max-height: 100vh !important;
                    z-index: 2147483640 !important;
                    background: #000000 !important;
                    transition: all 0.32s cubic-bezier(0.2, 0.8, 0.2, 1) !important;
                }

                /* ================= アンビエントライト有効時のフローティング・シネマフレーム ================= */
                html.zentube-zen-active.zentube-ambilight-active.zentube-site-yt #movie_player,
                html.zentube-zen-active.zentube-ambilight-active.zentube-site-bili #bilibili-player,
                html.zentube-zen-active.zentube-ambilight-active.zentube-site-bili .bpx-player-container,
                html.zentube-zen-active.zentube-ambilight-active.zentube-site-nico [data-zentube-nico-player="1"],
                html.zentube-zen-active.zentube-ambilight-active.zentube-site-abema [data-zentube-nico-player="1"],
                html.zentube-zen-active.zentube-ambilight-active.zentube-site-twitch .persistent-player,
                html.zentube-zen-active.zentube-ambilight-active.zentube-site-twitch .video-player__container {
                    top: 32px !important;
                    left: 46px !important;
                    width: calc(100vw - 92px) !important;
                    height: calc(100vh - 64px) !important;
                    max-width: calc(100vw - 92px) !important;
                    max-height: calc(100vh - 64px) !important;
                    border-radius: 16px !important;
                    overflow: hidden !important;
                    box-shadow: 0 22px 65px rgba(0, 0, 0, 0.78), 0 0 0 1px rgba(255, 255, 255, 0.14) !important;
                }

                /* ================= 💬 ニコニコ風 弾幕コメントオーバーレイ ================= */
                #zentube-danmaku-layer {
                    display: none;
                    position: fixed !important;
                    inset: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    z-index: 2147483646 !important;
                    pointer-events: none !important;
                    overflow: hidden !important;
                }

                html.zentube-danmaku-active #zentube-danmaku-layer {
                    display: block !important;
                }

                html.zentube-zen-active.zentube-ambilight-active #zentube-danmaku-layer {
                    top: 32px !important;
                    left: 46px !important;
                    width: calc(100vw - 92px) !important;
                    height: calc(100vh - 64px) !important;
                    border-radius: 16px !important;
                }

                .zentube-danmaku-item {
                    position: absolute !important;
                    left: 100% !important;
                    white-space: nowrap !important;
                    font-family: "Yu Gothic UI", "Meiryo", "MS PGothic", sans-serif !important;
                    font-weight: 800 !important;
                    font-size: 25px !important;
                    line-height: 1.2 !important;
                    color: #FFFFFF;
                    opacity: 0.92;
                    text-shadow:
                        -1.5px -1.5px 0 rgba(0, 0, 0, 0.94),
                         1.5px -1.5px 0 rgba(0, 0, 0, 0.94),
                        -1.5px  1.5px 0 rgba(0, 0, 0, 0.94),
                         1.5px  1.5px 0 rgba(0, 0, 0, 0.94),
                         0px    2px   5px rgba(0, 0, 0, 0.75) !important;
                    pointer-events: none !important;
                    will-change: transform !important;
                    animation: zentube-danmaku-flow 7.4s linear forwards !important;
                }

                html.zentube-danmaku-bold .zentube-danmaku-item {
                    font-size: 31px !important;
                    opacity: 1.0 !important;
                }

                html.zentube-danmaku-subtle .zentube-danmaku-item {
                    font-size: 20px !important;
                    opacity: 0.62 !important;
                }

                .zentube-danmaku-item.zentube-danmaku-user {
                    padding: 2px 10px !important;
                    border-radius: 8px !important;
                    border: 1.5px solid rgba(250, 204, 21, 0.85) !important;
                    background: rgba(15, 23, 42, 0.45) !important;
                }

                /* ================= Prismil 独自プレイヤー（埋め込み）: 動画だけを映す ================= */
                html.prismil-embed,
                html.prismil-embed body {
                    background: #000 !important;
                    overflow: hidden !important;
                }
                html.prismil-has-video body * {
                    visibility: hidden !important;
                }
                html.prismil-has-video video.prismil-main,
                html.prismil-has-video .prismil-overlay-canvas,
                html.prismil-has-video #zentube-danmaku-layer,
                html.prismil-has-video #zentube-danmaku-layer * {
                    visibility: visible !important;
                }
                html.prismil-has-video video.prismil-main {
                    position: fixed !important;
                    inset: 0 !important;
                    left: 0 !important;
                    top: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    max-width: none !important;
                    max-height: none !important;
                    margin: 0 !important;
                    object-fit: contain !important;
                    background: transparent !important;
                    z-index: 2147483640 !important;
                }
                html.prismil-has-video .prismil-overlay-canvas {
                    position: fixed !important;
                    inset: 0 !important;
                    width: 100vw !important;
                    height: 100vh !important;
                    object-fit: contain !important;
                    z-index: 2147483643 !important;
                    pointer-events: none !important;
                }
                html.prismil-embed #zentube-ambilight-backdrop {
                    display: none !important;
                }

                html.zentube-video-paused .zentube-danmaku-item {
                    animation-play-state: paused !important;
                }

                @keyframes zentube-danmaku-flow {
                    0%   { transform: translate3d(0, 0, 0); }
                    100% { transform: translate3d(calc(-100vw - 130%), 0, 0); }
                }
            `;

            function postToHost(msg) {
                try {
                    if (inPrismil) {
                        window.parent.postMessage({ __prismil: 'host', msg: msg }, '*');
                        return;
                    }
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage(msg);
                    }
                } catch {}
            }

            function formatSec(sec) {
                if (sec === null || sec === undefined || isNaN(sec)) return '--:--';
                const s = Math.floor(sec);
                const m = Math.floor(s / 60);
                const rem = s % 60;
                return m + ':' + (rem < 10 ? '0' + rem : rem);
            }

            function ensureStyleAndAmbilightDOM() {
                if (!document.getElementById(styleId) && document.head) {
                    const style = document.createElement('style');
                    style.id = styleId;
                    style.textContent = css;
                    document.head.appendChild(style);
                }
                if (!document.getElementById(ambiId) && document.body) {
                    const backdrop = document.createElement('div');
                    backdrop.id = ambiId;

                    const canvas = document.createElement('canvas');
                    canvas.id = ambiCanvasId;
                    canvas.width = 72;
                    canvas.height = 42;

                    const vignette = document.createElement('div');
                    vignette.id = 'zentube-ambilight-vignette';

                    backdrop.appendChild(canvas);
                    backdrop.appendChild(vignette);
                    document.body.appendChild(backdrop);
                }
                if (!document.getElementById(danmakuLayerId) && document.body) {
                    const dmLayer = document.createElement('div');
                    dmLayer.id = danmakuLayerId;
                    document.body.appendChild(dmLayer);
                }
            }

            // 💬 弾幕コメントを1件生成して右から左へ流す (11レーン衝突回避付き)
            function spawnDanmaku(text, color, isUser) {
                if (window.__zentubeDanmakuStyle === 'off' && !isUser) return;
                const layer = document.getElementById(danmakuLayerId);
                if (!layer || !text) return;

                const clean = String(text).replace(/\s+/g, ' ').trim();
                if (!clean) return;

                // 画面上の最大同時表示数を45件に制限して高FPSを維持
                if (layer.childElementCount > 45 && !isUser) return;

                const now = performance.now();
                let pickedLane = -1;
                for (let i = 0; i < laneBusyUntil.length; i++) {
                    if (now >= laneBusyUntil[i]) {
                        pickedLane = i;
                        break;
                    }
                }
                if (pickedLane === -1) {
                    pickedLane = Math.floor(Math.random() * laneBusyUntil.length);
                }

                // 文字数に応じてそのレーンの次の発射可能時刻を設定（重なり防止）
                const lockMs = Math.min(2600, 950 + clean.length * 42);
                laneBusyUntil[pickedLane] = now + lockMs;

                const el = document.createElement('div');
                el.className = isUser ? 'zentube-danmaku-item zentube-danmaku-user' : 'zentube-danmaku-item';
                el.textContent = clean;
                el.style.color = color || '#FFFFFF';
                el.style.top = (6 + pickedLane * 7.8) + '%';

                el.addEventListener('animationend', () => {
                    try { el.remove(); } catch {}
                });

                layer.appendChild(el);
            }

            // Twitch / YouTube Live / ABEMA などのリアルタイムチャットDOMから新規コメントを抽出して弾幕化
            function harvestLiveDomComments() {
                if (window.__zentubeDanmakuStyle === 'off') return;
                const host = location.hostname;

                if (host.includes('twitch.tv')) {
                    const nodes = document.querySelectorAll('[data-a-target="chat-line-message-body"], .vod-message .text-fragment');
                    let spawnedThisTick = 0;
                    nodes.forEach(node => {
                        if (node.__zentubeSeen) return;
                        node.__zentubeSeen = true;
                        if (spawnedThisTick < 3) {
                            const txt = (node.textContent || '').trim();
                            if (txt.length >= 1 && txt.length <= 75) {
                                spawnDanmaku(txt, '#FFFFFF', false);
                                spawnedThisTick++;
                            }
                        }
                    });
                } else if (host.includes('youtube.com')) {
                    try {
                        const chatFrame = document.querySelector('iframe#chatframe');
                        const chatDoc = chatFrame && chatFrame.contentDocument ? chatFrame.contentDocument : document;
                        const items = chatDoc.querySelectorAll('yt-live-chat-text-message-renderer #message');
                        let spawned = 0;
                        items.forEach(node => {
                            if (node.__zentubeSeen) return;
                            node.__zentubeSeen = true;
                            if (spawned < 3) {
                                const txt = (node.textContent || '').trim();
                                if (txt.length >= 1 && txt.length <= 75) {
                                    spawnDanmaku(txt, '#FFFFFF', false);
                                    spawned++;
                                }
                            }
                        });
                    } catch {}
                } else if (host.includes('abema.tv')) {
                    const nodes = document.querySelectorAll('[class*="CommentItem"], [class*="comment-item"]');
                    let spawned = 0;
                    nodes.forEach(node => {
                        if (node.__zentubeSeen) return;
                        node.__zentubeSeen = true;
                        if (spawned < 3) {
                            const txt = (node.textContent || '').trim();
                            if (txt.length >= 1 && txt.length <= 75) {
                                spawnDanmaku(txt, '#FFFFFF', false);
                                spawned++;
                            }
                        }
                    });
                }
            }

            // 動画再生位置に合わせたタイムスタンプ弾幕＆一般コメント弾幕のスケジューラ
            function tickDanmakuEngine() {
                const video = document.querySelector('video, audio');
                const root = document.documentElement;
                const isPaused = video ? video.paused : false;
                if (root) {
                    root.classList.toggle('zentube-video-paused', isPaused);
                }

                if (window.__zentubeDanmakuStyle === 'off' || isPaused || wasAdPlaying) return;

                harvestLiveDomComments();

                const curTime = video ? (video.currentTime || 0) : 0;

                // 1. タイムスタンプ付きコメント＆ユーザー保存弾幕の発射判定
                if (window.__zentubeTimedComments && window.__zentubeTimedComments.length > 0 && video) {
                    if (Math.abs(curTime - lastVideoTime) > 3.5) {
                        // シークされた場合は現在時刻より後のコメントを再有効化
                        window.__zentubeTimedComments.forEach(c => {
                            c.__fired = (c.timeSeconds < curTime - 0.5);
                        });
                    }
                    lastVideoTime = curTime;

                    for (const c of window.__zentubeTimedComments) {
                        if (!c.__fired && c.timeSeconds >= curTime - 1.2 && c.timeSeconds <= curTime + 0.6) {
                            c.__fired = true;
                            spawnDanmaku(c.text, c.color || '#FFFFFF', Boolean(c.isUser));
                        }
                    }
                }

                // 2. 一般コメントプールから一定間隔で自然に弾幕を流す
                const pool = window.__zentubeGeneralComments;
                if (pool && pool.length > 0) {
                    const now = performance.now();
                    const intervalMs = window.__zentubeDanmakuStyle === 'bold' ? 750 : (window.__zentubeDanmakuStyle === 'subtle' ? 1750 : 1150);
                    if (now - lastDanmakuSpawn >= intervalMs) {
                        lastDanmakuSpawn = now;
                        const item = pool[window.__zentubeGeneralCursor % pool.length];
                        window.__zentubeGeneralCursor = (window.__zentubeGeneralCursor + 1) % pool.length;
                        if (item && item.text) {
                            spawnDanmaku(item.text, item.color || '#FFFFFF', false);
                        }
                    }
                }
            }

            // リアルタイム・アンビエントライト描画ループ (50ms / 20fps + 残像補間)
            // 画面全体の色合わせ用：動画を 8x4 に縮めて「左・全体・右」の3色を取る
            const ambientSampleCanvas = document.createElement('canvas');
            ambientSampleCanvas.width = 8;
            ambientSampleCanvas.height = 4;

            function sampleAmbientColors(video) {
                const sctx = ambientSampleCanvas.getContext('2d', { willReadFrequently: true });
                if (!sctx) return;
                sctx.drawImage(video, 0, 0, 8, 4);
                const d = sctx.getImageData(0, 0, 8, 4).data; // Cross-Origin の動画では例外 → 呼び出し側で無視

                const avg = (x0, x1) => {
                    let r = 0, g = 0, b = 0, n = 0;
                    for (let y = 0; y < 4; y++) {
                        for (let x = x0; x <= x1; x++) {
                            const i = (y * 8 + x) * 4;
                            r += d[i]; g += d[i + 1]; b += d[i + 2]; n++;
                        }
                    }
                    return `${Math.round(r / n)},${Math.round(g / n)},${Math.round(b / n)}`;
                };
                postToHost(`ambient_rgb3:${avg(0, 2)};${avg(0, 7)};${avg(5, 7)}`);
            }

            function renderAmbilightFrame() {
                if (window.__zentubeAmbilight === 'off' || !isVideoWatchPage()) return;
                const video = document.querySelector('video');
                if (!video || video.readyState < 2) return;

                // 250msごとに動画の色を取り出して、アプリ画面全体の色合わせに使う（Zenモードに関係なく動く）
                const now = performance.now();
                if (now - lastColorSend > 250) {
                    lastColorSend = now;
                    try { sampleAmbientColors(video); } catch {}
                }

                // 以下は Zen モード時に動画のまわりを光らせる演出
                if (!window.__zentubeZenMode || inPrismil) return; // 独自プレイヤーでは画面側が光らせる
                const canvas = document.getElementById(ambiCanvasId);
                if (!canvas) return;
                try {
                    const ctx = canvas.getContext('2d');
                    if (!ctx) return;
                    // 前フレームと滑らかにブレンドすることで目に優しいアンビエント発光を実現
                    ctx.globalAlpha = 0.28;
                    ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
                } catch {}
            }

            function isVideoWatchPage() {
                if (inPrismil) return true;
                const host = location.hostname;
                const path = location.pathname;
                if (host.includes('youtube.com')) return path.startsWith('/watch') || path.startsWith('/shorts') || path.startsWith('/live');
                if (host.includes('nicovideo.jp')) return path.startsWith('/watch');
                // 日本から開くと /ja/video/... のように言語付きのURLになるので、それも動画ページとして扱う
                if (host.includes('bilibili.com')) return /^\/(?:[a-z]{2}(?:-[a-z]{2,4})?\/)?(?:video|bangumi\/play)\//i.test(path);
                if (host.includes('twitch.tv')) {
                    return path !== '/' && !path.startsWith('/directory') && !path.startsWith('/search') && !path.startsWith('/downloads');
                }
                if (host.includes('abema.tv')) {
                    return path.startsWith('/now-on-air') || path.startsWith('/video/episode') || path.startsWith('/channels') || path.startsWith('/live-event');
                }
                if (host.includes('soundcloud.com') || host.includes('dailymotion.com')) {
                    return true;
                }
                return false;
            }

            // 動画プレイヤーが実際にページにあるか。
            // ニコニコのログイン必須（センシティブ判定）動画などは、動画の代わりに案内画面が出るので、
            // その間はプレイヤーだけを抜き出す表示にせず、サイトの画面をそのまま見せる（真っ黒にならないように）
            function isPlayerReady() {
                const host = location.hostname;
                if (host.includes('nicovideo.jp') || host.includes('abema.tv') || host.includes('bilibili.com')) {
                    return document.querySelector('video') !== null;
                }
                return true;
            }

            function tagNiconicoPlayer() {
                const host = location.hostname;
                if (!host.includes('nicovideo.jp') && !host.includes('abema.tv')) return;
                const video = document.querySelector('video');
                if (!video) return;

                const existing = document.querySelector('[data-zentube-nico-player="1"]');
                if (existing && existing.contains(video)) return;

                let current = video.parentElement;
                let candidate = current;
                while (current && current !== document.body && current !== document.documentElement) {
                    const hasCanvas = current.querySelector('canvas') !== null;
                    const hasButtons = current.querySelectorAll('button').length >= 2;
                    const rect = current.getBoundingClientRect();

                    if (hasCanvas && hasButtons) {
                        candidate = current;
                        break;
                    }
                    if (host.includes('abema.tv') && hasButtons && rect.width >= window.innerWidth * 0.45) {
                        candidate = current;
                        break;
                    }
                    if (rect.width > 0 && rect.width <= window.innerWidth * 0.92) {
                        candidate = current;
                    }
                    current = current.parentElement;
                }

                if (candidate) {
                    document.querySelectorAll('[data-zentube-nico-player]').forEach(el => el.removeAttribute('data-zentube-nico-player'));
                    candidate.setAttribute('data-zentube-nico-player', '1');
                }
            }

            // Web Audio API による音量ブースト（最大300%）＆ 音質イコライザー
            function setupAudioChain(video) {
                if (!video) return;
                if (window.__zentubeAudioGain === 1.0 && window.__zentubeAudioPreset === 'normal' && !video.__zentubeAudioCtx) {
                    return;
                }
                try {
                    if (!video.__zentubeAudioCtx) {
                        const AudioCtx = window.AudioContext || window.webkitAudioContext;
                        if (!AudioCtx) return;
                        const ctx = new AudioCtx();
                        const source = ctx.createMediaElementSource(video);
                        const bassFilter = ctx.createBiquadFilter();
                        bassFilter.type = 'lowshelf';
                        bassFilter.frequency.value = 180;

                        const vocalFilter = ctx.createBiquadFilter();
                        vocalFilter.type = 'peaking';
                        vocalFilter.frequency.value = 2200;
                        vocalFilter.Q.value = 1.0;

                        const gainNode = ctx.createGain();

                        source.connect(bassFilter);
                        bassFilter.connect(vocalFilter);
                        vocalFilter.connect(gainNode);
                        gainNode.connect(ctx.destination);

                        video.__zentubeAudioCtx = ctx;
                        video.__zentubeBassFilter = bassFilter;
                        video.__zentubeVocalFilter = vocalFilter;
                        video.__zentubeGainNode = gainNode;
                    }

                    if (video.__zentubeAudioCtx.state === 'suspended') {
                        video.__zentubeAudioCtx.resume().catch(() => {});
                    }

                    video.__zentubeGainNode.gain.value = window.__zentubeAudioGain || 1.0;

                    const preset = window.__zentubeAudioPreset || 'normal';
                    if (preset === 'bass') {
                        video.__zentubeBassFilter.gain.value = 8.0;
                        video.__zentubeVocalFilter.gain.value = 0.0;
                    } else if (preset === 'vocal') {
                        video.__zentubeBassFilter.gain.value = -3.0;
                        video.__zentubeVocalFilter.gain.value = 7.0;
                    } else if (preset === 'night') {
                        video.__zentubeBassFilter.gain.value = -5.0;
                        video.__zentubeVocalFilter.gain.value = 4.0;
                    } else {
                        video.__zentubeBassFilter.gain.value = 0.0;
                        video.__zentubeVocalFilter.gain.value = 0.0;
                    }
                } catch {}
            }

            // 動画広告の自動スキップ＆高速早送り
            function handleVideoAds() {
                if (!window.__zentubeAdBlock) return;

                const host = location.hostname;

                if (host.includes('youtube.com')) {
                    const skipSelectors = [
                        '.ytp-ad-skip-button',
                        '.ytp-ad-skip-button-modern',
                        '.ytp-skip-ad-button',
                        '.ytp-ad-overlay-close-button',
                        '.ytp-ad-skip-button-slot button'
                    ];
                    for (const sel of skipSelectors) {
                        const btn = document.querySelector(sel);
                        if (btn) {
                            btn.click();
                            postToHost('ad_skipped');
                        }
                    }

                    const player = document.querySelector('#movie_player');
                    const video = document.querySelector('video.html5-main-video');
                    const isAd = player && (player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting'));

                    if (isAd && video) {
                        wasAdPlaying = true;
                        video.muted = true;
                        if (isFinite(video.duration) && video.duration > 0.5 && video.currentTime < video.duration - 0.2) {
                            video.currentTime = video.duration;
                            postToHost('ad_skipped');
                        } else {
                            video.playbackRate = 16.0;
                        }
                    } else if (wasAdPlaying && video) {
                        wasAdPlaying = false;
                        video.muted = false;
                        video.playbackRate = window.__zentubePlaybackRate || 1.0;
                    }
                }

                if (host.includes('nicovideo.jp')) {
                    const nicoSkipBtns = document.querySelectorAll('button[aria-label*="スキップ"], [class*="SkipButton"]');
                    nicoSkipBtns.forEach(btn => {
                        try {
                            btn.click();
                            postToHost('ad_skipped');
                        } catch {}
                    });
                }

                if (host.includes('bilibili.com')) {
                    const mask = document.querySelector('.bili-mini-mask, .bili-login-card');
                    if (mask) {
                        mask.remove();
                        const v = document.querySelector('video');
                        if (v && v.paused) v.play().catch(() => {});
                    }
                }

                if (host.includes('youtube.com')) {
                    const upsell = document.querySelector('tp-yt-paper-dialog:has(yt-upsell-dialog-renderer), ytd-consent-bump-v2-lightbox');
                    if (upsell) {
                        upsell.remove();
                        const v = document.querySelector('video.html5-main-video');
                        if (v && v.paused) v.play().catch(() => {});
                    }
                }

                if (host.includes('twitch.tv')) {
                    const gateBtn = document.querySelector('button[data-a-target="content-classification-gate-overlay-start-watching-button"]');
                    if (gateBtn) {
                        try { gateBtn.click(); } catch {}
                    }
                }
            }

            // 動画・音源の再生速度・ループ・ABリピート・音声エフェクトの同期
            function syncVideoSettings() {
                const video = document.querySelector('video, audio');
                if (!video) return;

                if (!wasAdPlaying && Math.abs(video.playbackRate - window.__zentubePlaybackRate) > 0.01) {
                    try { video.playbackRate = window.__zentubePlaybackRate; } catch {}
                }

                if (video.loop !== Boolean(window.__zentubeLoop)) {
                    video.loop = Boolean(window.__zentubeLoop);
                }

                if (!wasAdPlaying && window.__zentubePointA !== null && window.__zentubePointB !== null) {
                    if (window.__zentubePointB > window.__zentubePointA + 0.5) {
                        if (video.currentTime >= window.__zentubePointB || video.currentTime < window.__zentubePointA - 0.5) {
                            video.currentTime = window.__zentubePointA;
                            if (video.paused) video.play().catch(() => {});
                        }
                    }
                }

                setupAudioChain(video);

                if (!video.__zentubeEndedBound) {
                    video.__zentubeEndedBound = true;
                    video.addEventListener('ended', () => {
                        if (wasAdPlaying) return;
                        if (window.__zentubePointA !== null && window.__zentubePointB !== null) {
                            video.currentTime = window.__zentubePointA;
                            video.play().catch(() => {});
                        } else if (window.__zentubeLoop) {
                            video.currentTime = 0;
                            video.play().catch(() => {});
                        } else {
                            postToHost('video_ended');
                        }
                    });
                }
            }

            function applyState() {
                ensureStyleAndAmbilightDOM();
                const root = document.documentElement;
                if (!root) return;

                const host = location.hostname;
                root.classList.toggle('zentube-site-yt', host.includes('youtube.com'));
                root.classList.toggle('zentube-site-nico', host.includes('nicovideo.jp'));
                root.classList.toggle('zentube-site-bili', host.includes('bilibili.com'));
                root.classList.toggle('zentube-site-twitch', host.includes('twitch.tv'));
                root.classList.toggle('zentube-site-abema', host.includes('abema.tv'));

                root.classList.toggle('prismil-embed', inPrismil);
                const shouldZen = !inPrismil && Boolean(window.__zentubeZenMode) && isVideoWatchPage() && isPlayerReady();
                const wasZen = root.classList.contains('zentube-zen-active');
                const ambiMode = window.__zentubeAmbilight || 'vivid';
                const shouldAmbi = shouldZen && ambiMode !== 'off';
                const dmStyle = window.__zentubeDanmakuStyle || 'normal';
                const shouldDanmaku = dmStyle !== 'off' && isVideoWatchPage();

                root.classList.toggle('zentube-zen-active', shouldZen);
                root.classList.toggle('zentube-ambilight-active', shouldAmbi);
                root.classList.toggle('zentube-ambilight-soft', shouldAmbi && ambiMode === 'soft');
                root.classList.toggle('zentube-ambilight-ultra', shouldAmbi && ambiMode === 'ultra');
                root.classList.toggle('zentube-danmaku-active', shouldDanmaku);
                root.classList.toggle('zentube-danmaku-bold', shouldDanmaku && dmStyle === 'bold');
                root.classList.toggle('zentube-danmaku-subtle', shouldDanmaku && dmStyle === 'subtle');
                root.classList.toggle('zentube-adblock-active', Boolean(window.__zentubeAdBlock));
                root.classList.toggle('zentube-block-shorts', Boolean(window.__zentubeBlockShorts));
                root.classList.toggle('zentube-mirror-active', Boolean(window.__zentubeMirror));
                root.classList.toggle('zentube-dim-active', Boolean(window.__zentubeDim));

                // ショート動画ページ (/shorts/xxx) に迷い込んだ場合は通常プレイヤーへ変換
                if (!inPrismil && window.__zentubeBlockShorts && host.includes('youtube.com') && location.pathname.startsWith('/shorts/')) {
                    const shortId = location.pathname.replace('/shorts/', '').split('/')[0].split('?')[0];
                    if (shortId) {
                        location.replace('https://www.youtube.com/watch?v=' + shortId);
                        return;
                    }
                }

                if (shouldZen) {
                    tagNiconicoPlayer();
                }

                // 表示の切り替えがあった時だけプレイヤーにサイズを計算し直させる
                // （確認画面の間は 0.25 秒ごとに呼ばれるので、毎回は送らない）
                if (wasZen !== shouldZen) {
                    window.dispatchEvent(new Event('resize'));
                }
            }

            window.setZenTubeMode = function(enabled) {
                window.__zentubeZenMode = Boolean(enabled);
                applyState();
            };

            window.setZenTubeAmbilight = function(mode) {
                window.__zentubeAmbilight = mode || 'off';
                applyState();
            };

            window.setZenTubeDanmaku = function(style) {
                window.__zentubeDanmakuStyle = style || 'off';
                if (window.__zentubeDanmakuStyle === 'off') {
                    const layer = document.getElementById(danmakuLayerId);
                    if (layer) layer.innerHTML = '';
                }
                applyState();
            };

            window.loadZenTubeDanmakuPool = function(comments) {
                const layer = document.getElementById(danmakuLayerId);
                if (layer) layer.innerHTML = '';
                window.__zentubeTimedComments = [];
                window.__zentubeGeneralComments = [];
                window.__zentubeGeneralCursor = 0;
                lastVideoTime = -1;

                if (!Array.isArray(comments)) return;
                for (const c of comments) {
                    const t = Number(c.TimeSeconds !== undefined ? c.TimeSeconds : c.timeSeconds);
                    const txt = c.Text || c.text || '';
                    const col = c.Color || c.color || '#FFFFFF';
                    const usr = Boolean(c.IsUser !== undefined ? c.IsUser : c.isUser);
                    if (!txt) continue;

                    if (!isNaN(t) && t >= 0) {
                        window.__zentubeTimedComments.push({ timeSeconds: t, text: txt, color: col, isUser: usr, __fired: false });
                    } else {
                        window.__zentubeGeneralComments.push({ text: txt, color: col });
                    }
                }
            };

            window.setZenTubeAdBlock = function(enabled) {
                window.__zentubeAdBlock = Boolean(enabled);
                applyState();
            };

            window.setZenTubeBlockShorts = function(enabled) {
                window.__zentubeBlockShorts = Boolean(enabled);
                applyState();
            };

            window.setZenTubePlaybackRate = function(rate) {
                window.__zentubePlaybackRate = Number(rate) || 1.0;
                syncVideoSettings();
            };

            window.setZenTubeLoop = function(loop) {
                window.__zentubeLoop = Boolean(loop);
                syncVideoSettings();
            };

            window.setZenTubeVideoFx = function(mirror, dim) {
                window.__zentubeMirror = Boolean(mirror);
                window.__zentubeDim = Boolean(dim);
                applyState();
            };

            window.setZenTubeAudioProfile = function(gain, preset) {
                window.__zentubeAudioGain = Number(gain) || 1.0;
                window.__zentubeAudioPreset = preset || 'normal';
                syncVideoSettings();
            };

            window.stepZenTubeABRepeat = function() {
                const video = document.querySelector('video, audio');
                if (!video) return;
                if (window.__zentubePointA === null) {
                    window.__zentubePointA = video.currentTime;
                    window.__zentubePointB = null;
                    postToHost('ab_state:A=' + formatSec(window.__zentubePointA) + ' ～ [Bを指定]');
                } else if (window.__zentubePointB === null) {
                    if (video.currentTime > window.__zentubePointA + 0.5) {
                        window.__zentubePointB = video.currentTime;
                        postToHost('ab_state:🔁 ' + formatSec(window.__zentubePointA) + ' ～ ' + formatSec(window.__zentubePointB));
                    } else {
                        window.__zentubePointA = video.currentTime;
                        postToHost('ab_state:A=' + formatSec(window.__zentubePointA) + ' ～ [Bを指定]');
                    }
                } else {
                    window.__zentubePointA = null;
                    window.__zentubePointB = null;
                    postToHost('ab_state:OFF');
                }
            };

            window.clearZenTubeABRepeat = function() {
                window.__zentubePointA = null;
                window.__zentubePointB = null;
                postToHost('ab_state:OFF');
            };

            window.pauseZenTubeVideo = function() {
                document.querySelectorAll('video, audio').forEach(v => {
                    try { v.pause(); } catch {}
                });
                if (location.hostname.includes('soundcloud.com')) {
                    const scBtn = document.querySelector('.playButton.playing, .playControl.playing');
                    if (scBtn) { try { scBtn.click(); } catch {} }
                }
            };

            window.toggleZenTubePlayPause = function() {
                if (location.hostname.includes('soundcloud.com')) {
                    const scBtn = document.querySelector('.playButton, .playControl');
                    if (scBtn) {
                        try { scBtn.click(); return; } catch {}
                    }
                }
                const v = document.querySelector('video, audio');
                if (!v) return;
                if (v.paused) v.play().catch(() => {});
                else v.pause();
            };

            // ================= Prismil 独自プレイヤーとのやりとり（埋め込みフレーム側） =================
            let prismilHasVideo = false;
            let prismilMainVideo = null;
            let prismilLastTitle = '';

            function pickMainVideo() {
                if (prismilMainVideo && prismilMainVideo.isConnected) return prismilMainVideo;
                let best = null, bestArea = -1;
                document.querySelectorAll('video').forEach(v => {
                    const r = v.getBoundingClientRect();
                    const area = r.width * r.height + (v.readyState >= 1 ? 1e7 : 0);
                    if (area > bestArea) { bestArea = area; best = v; }
                });
                if (best) {
                    document.querySelectorAll('video.prismil-main').forEach(v => { if (v !== best) v.classList.remove('prismil-main'); });
                    best.classList.add('prismil-main');
                    if (best.readyState >= 1) prismilMainVideo = best;
                }
                return best;
            }

            function forwardToChildren(data) {
                for (let i = 0; i < window.frames.length; i++) {
                    try { window.frames[i].postMessage(data, '*'); } catch {}
                }
            }

            function isChildFrame(win) {
                for (let i = 0; i < window.frames.length; i++) {
                    if (window.frames[i] === win) return true;
                }
                return false;
            }

            function runPrismilCommand(d) {
                const v = pickMainVideo();
                if (!v) { forwardToChildren(d); return; }
                const num = Number(d.value);
                switch (d.op) {
                    case 'toggle': if (v.paused) v.play().catch(() => {}); else v.pause(); break;
                    case 'play': v.play().catch(() => {}); break;
                    case 'pause': v.pause(); break;
                    case 'seek':
                        if (isFinite(v.duration) && isFinite(num)) v.currentTime = Math.max(0, Math.min(v.duration - 0.25, num));
                        break;
                    case 'seekBy':
                        if (isFinite(num)) {
                            const max = isFinite(v.duration) ? v.duration - 0.25 : v.currentTime + num;
                            v.currentTime = Math.max(0, Math.min(max, v.currentTime + num));
                        }
                        break;
                    case 'volume':
                        if (isFinite(num)) { v.volume = Math.max(0, Math.min(1, num)); v.muted = v.volume === 0; }
                        break;
                    case 'toggleMute':
                        v.muted = !v.muted;
                        if (!v.muted && v.volume === 0) v.volume = 0.5;
                        break;
                }
            }

            function isYouTubeEmbedError() {
                if (!location.hostname.includes('youtube.com')) return false;
                const e = document.querySelector('.ytp-error');
                return Boolean(e && e.getClientRects().length > 0 && getComputedStyle(e).display !== 'none');
            }

            // ニコニコの公式コメントはキャンバスに描かれるので、動画と一緒に映す
            function tagOverlayCanvases(video) {
                if (!location.hostname.includes('nicovideo.jp') || !video || !video.parentElement) return;
                const vr = video.parentElement.getBoundingClientRect();
                document.querySelectorAll('canvas').forEach(c => {
                    if ((c.id && c.id.indexOf('zentube') === 0) || c.classList.contains('prismil-overlay-canvas')) return;
                    const r = c.getBoundingClientRect();
                    if (r.width >= vr.width * 0.6 && r.height >= vr.height * 0.6) c.classList.add('prismil-overlay-canvas');
                });
            }

            // ニコニコの埋め込みは autoplay 指定を無視して再生ボタン待ちになるので、真ん中の再生ボタンを押す
            let prismilAutoStartTries = 0;
            let prismilAutoStartAt = performance.now() + 1500;
            function autoStartEmbed(v) {
                if (!location.hostname.includes('embed.nicovideo.jp')) return;
                if (prismilHasVideo || prismilAutoStartTries >= 4 || performance.now() < prismilAutoStartAt) return;
                if (v && (v.readyState >= 1 || !v.paused)) return;
                prismilAutoStartTries++;
                prismilAutoStartAt = performance.now() + 2500;
                try {
                    const el = document.elementFromPoint(window.innerWidth / 2, window.innerHeight / 2);
                    const btn = el && el.closest('button');
                    if (btn) btn.click();
                } catch {}
            }

            function prismilEmbedTick() {
                const root = document.documentElement;
                const v = pickMainVideo();
                autoStartEmbed(v);
                const err = isYouTubeEmbedError();
                if (err) {
                    prismilHasVideo = false;
                } else if (v && (v.readyState >= 1 || !v.paused)) {
                    prismilHasVideo = true;
                }
                if (root) root.classList.toggle('prismil-has-video', prismilHasVideo);
                if (prismilHasVideo) tagOverlayCanvases(v);

                if (!v && !err) return;
                try {
                    window.parent.postMessage({
                        __prismil: 'state',
                        hasVideo: prismilHasVideo,
                        error: err,
                        t: v ? v.currentTime : 0,
                        d: v ? v.duration : 0,
                        paused: v ? v.paused : true,
                        vol: v ? v.volume : 1,
                        muted: v ? v.muted : false
                    }, '*');
                } catch {}

                if (prismilHasVideo && document.title && document.title !== prismilLastTitle) {
                    prismilLastTitle = document.title;
                    postToHost('doc_title:' + document.title);
                }
            }

            if (inPrismil) {
                window.addEventListener('message', (e) => {
                    const d = e.data;
                    if (!d || typeof d !== 'object' || !d.__prismil) return;
                    if (e.source === window.parent || e.source === window.top) {
                        if (d.__prismil === 'call') {
                            if (typeof d.name === 'string' && /^(set|load|step|clear|pause|toggle)ZenTube/.test(d.name) &&
                                typeof window[d.name] === 'function') {
                                try { window[d.name].apply(window, Array.isArray(d.args) ? d.args : []); } catch {}
                            }
                            forwardToChildren(d);
                        } else if (d.__prismil === 'cmd') {
                            runPrismilCommand(d);
                        }
                    } else if (isChildFrame(e.source)) {
                        // 入れ子のフレーム（広告など）からの連絡は、そのまま上へ渡す
                        if (d.__prismil === 'host' || d.__prismil === 'state' || d.__prismil === 'ready') {
                            try { window.parent.postMessage(d, '*'); } catch {}
                        }
                    }
                });
                try { window.parent.postMessage({ __prismil: 'ready' }, '*'); } catch {}
                setInterval(prismilEmbedTick, 250);
            }

            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', applyState);
            } else {
                applyState();
            }

            // アンビエントライト描画ループ (55ms間隔)
            setInterval(renderAmbilightFrame, 55);

            // 広告検知・設定同期・弾幕スケジューラループ (250ms間隔)
            setInterval(() => {
                ensureStyleAndAmbilightDOM();
                handleVideoAds();
                syncVideoSettings();
                tickDanmakuEngine();
                if (window.__zentubeZenMode && isVideoWatchPage()) {
                    tagNiconicoPlayer();
                    // 動画プレイヤーが現れた／消えた（確認画面に切り替わった）時に表示を合わせる
                    if (document.documentElement.classList.contains('zentube-zen-active') !== isPlayerReady()) {
                        applyState();
                    }
                }
            }, 250);
        })();
        """;
}
