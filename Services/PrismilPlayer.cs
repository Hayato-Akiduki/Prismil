using System.IO;
using System.Text.RegularExpressions;
using ZenTube.Models;

namespace ZenTube.Services;

/// <summary>
/// Prismil 独自プレイヤー。
/// 各サイトの視聴ページを開く代わりに、アプリ内のプレイヤー画面（player.prismil.local）を開き、
/// その中に各サイトの「埋め込みプレイヤー」を入れる。サイトの画面は見せず、動画だけを映し、
/// 操作バーはどのサイトでも同じものを使う。
/// </summary>
public static class PrismilPlayer
{
    public const string HostName = "player.prismil.local";

    /// <summary>プレイヤー画面のファイルを置き、WebView2 から https://player.prismil.local/ で開けるようにする</summary>
    public static void Install(Microsoft.Web.WebView2.Core.CoreWebView2 core, string dataRoot)
    {
        var dir = Path.Combine(dataRoot, "Player");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "player.html"), ShellHtml);
        core.SetVirtualHostNameToFolderMapping(
            HostName, dir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
    }

    public static bool IsPlayerUrl(string? url) =>
        url != null && url.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase);

    /// <summary>独自プレイヤーで開くURL。埋め込みに対応していないもの（ABEMA・ラジオ等）は null</summary>
    public static string? BuildPlayerUrl(VideoItem item)
    {
        var embed = BuildEmbedUrl(item);
        if (embed == null) return null;
        return $"https://{HostName}/player.html" +
               $"?site={item.Site.ToString().ToLowerInvariant()}" +
               $"&src={Uri.EscapeDataString(embed)}" +
               $"&fallback={Uri.EscapeDataString(item.WatchUrl)}";
    }

    private static string? BuildEmbedUrl(VideoItem item)
    {
        var id = item.Id ?? "";
        switch (item.Site)
        {
            case SiteType.YouTube:
                if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$")) return null;
                return $"https://www.youtube.com/embed/{id}?autoplay=1&controls=0&rel=0&playsinline=1" +
                       "&iv_load_policy=3&disablekb=1&fs=0&modestbranding=1&enablejsapi=1" +
                       $"&origin={Uri.EscapeDataString("https://" + HostName)}";

            case SiteType.Niconico:
                if (!Regex.IsMatch(id, @"^(sm|so|nm)\d+$", RegexOptions.IgnoreCase)) return null;
                return $"https://embed.nicovideo.jp/watch/{id}?autoplay=1";

            case SiteType.Bilibili:
                if (!Regex.IsMatch(id, @"^BV[0-9A-Za-z]{10}$")) return null;
                return $"https://player.bilibili.com/player.html?bvid={id}&autoplay=1&high_quality=1&danmaku=0";

            case SiteType.Twitch:
                if (item.WatchUrl.Contains("/videos/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Regex.IsMatch(id, @"^\d+$")) return null;
                    return $"https://player.twitch.tv/?video=v{id}&parent={HostName}&autoplay=true&muted=false";
                }
                if (!Regex.IsMatch(id, @"^[A-Za-z0-9_]{2,40}$")) return null;
                return $"https://player.twitch.tv/?channel={id}&parent={HostName}&autoplay=true&muted=false";

            // Dailymotion は埋め込みURLが「Forbidden」になる・広告読み込みで止まるため、今は今まで通りの表示にする

            case SiteType.SoundCloud:
                if (!item.WatchUrl.StartsWith("https://soundcloud.com/", StringComparison.OrdinalIgnoreCase)) return null;
                return $"https://w.soundcloud.com/player/?url={Uri.EscapeDataString(item.WatchUrl)}" +
                       "&auto_play=true&visual=true&hide_related=true&show_comments=false&show_reposts=false";

            default:
                // ABEMA は埋め込みプレイヤーが無い。Podcast・ラジオは音声だけなので今まで通り
                return null;
        }
    }

    private const string ShellHtml = """
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<title>Prismil Player</title>
<style>
  :root { --accent: #818cf8; }
  html, body { margin: 0; height: 100%; background: #030407; overflow: hidden; color: #f1f5f9;
    font-family: "Yu Gothic UI", "Segoe UI", "Meiryo", sans-serif; user-select: none; }
  #glow { position: fixed; inset: 0; opacity: 0; transition: opacity .6s ease, background .45s linear; pointer-events: none; }
  body.ambi #glow { opacity: 1; }
  body.ambi-soft #glow { opacity: .6; }
  #stage { position: fixed; inset: 0; background: #000; overflow: hidden;
    transition: inset .32s cubic-bezier(.2,.8,.2,1), border-radius .32s, box-shadow .32s; }
  body.ambi:not(.fs) #stage { inset: 32px 46px; border-radius: 16px;
    box-shadow: 0 22px 65px rgba(0,0,0,.78), 0 0 0 1px rgba(255,255,255,.14); }
  #frame { position: absolute; inset: 0; width: 100%; height: 100%; border: 0; background: transparent; }
  #shield { position: absolute; inset: 0; display: none; }
  body.has-video #shield { display: block; }
  body.idle #shield { cursor: none; }

  #loading { position: absolute; inset: 0; display: grid; place-items: center; pointer-events: none; transition: opacity .3s; }
  body.has-video #loading, body.problem #loading { opacity: 0; }
  .spinner { width: 42px; height: 42px; border-radius: 50%; border: 3px solid rgba(255,255,255,.15);
    border-top-color: var(--accent); animation: spin .9s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }

  #flash { position: absolute; left: 50%; top: 50%; width: 74px; height: 74px; margin: -37px 0 0 -37px; border-radius: 50%;
    background: rgba(0,0,0,.55); display: grid; place-items: center; opacity: 0; transform: scale(.8); pointer-events: none; }
  #flash.on { animation: flash .55s ease-out; }
  @keyframes flash { 0% { opacity: .95; transform: scale(.8); } 100% { opacity: 0; transform: scale(1.25); } }
  #flash svg { width: 34px; height: 34px; }

  #bar { position: absolute; left: 0; right: 0; bottom: 0; padding: 34px 14px 8px; display: none; opacity: 0;
    background: linear-gradient(to bottom, transparent, rgba(0,0,0,.82)); transition: opacity .25s; }
  body.has-video #bar { display: block; }
  body.show-ui #bar { opacity: 1; }
  .row { display: flex; align-items: center; gap: 6px; }
  .spacer { flex: 1; }
  button { background: none; border: 0; color: #fff; width: 38px; height: 38px; border-radius: 9px; cursor: pointer;
    display: grid; place-items: center; padding: 0; }
  button:hover { background: rgba(255,255,255,.13); }
  button svg { width: 22px; height: 22px; fill: currentColor; }
  #time { font-size: 13px; font-variant-numeric: tabular-nums; opacity: .92; margin-left: 4px; white-space: nowrap; }
  #liveTag { display: none; font-size: 11px; font-weight: 800; letter-spacing: .08em; color: #fff; background: #ef4444;
    padding: 2px 7px; border-radius: 5px; margin-left: 6px; }
  body.live #liveTag { display: inline-block; }
  body.live #time, body.live #seekWrap { display: none; }

  #seekWrap { position: relative; height: 16px; margin: 0 4px 4px; cursor: pointer; }
  #seekTrack { position: absolute; left: 0; right: 0; top: 6px; height: 4px; border-radius: 4px; background: rgba(255,255,255,.22);
    transition: height .15s, top .15s; }
  #seekWrap:hover #seekTrack { top: 5px; height: 6px; }
  #seekFill { position: absolute; left: 0; top: 0; bottom: 0; width: 0; border-radius: 4px;
    background: linear-gradient(90deg, #6366f1, #ec4899); }
  #seekKnob { position: absolute; top: 50%; width: 13px; height: 13px; margin: -6.5px 0 0 -6.5px; border-radius: 50%;
    background: #fff; left: 0; transform: scale(0); transition: transform .15s; }
  #seekWrap:hover #seekKnob, #seekWrap.drag #seekKnob { transform: scale(1); }
  #seekTip { position: absolute; bottom: 20px; padding: 3px 7px; border-radius: 6px; background: rgba(15,23,42,.92);
    font-size: 12px; font-variant-numeric: tabular-nums; transform: translateX(-50%); display: none; pointer-events: none; }
  #seekWrap:hover #seekTip, #seekWrap.drag #seekTip { display: block; }

  #volWrap { display: flex; align-items: center; }
  #vol { width: 0; opacity: 0; transition: width .2s, opacity .2s; accent-color: #a5b4fc; height: 4px; cursor: pointer; }
  #volWrap:hover #vol, #vol:focus { width: 84px; opacity: 1; margin: 0 6px; }

  #fallback { position: absolute; top: 14px; right: 14px; width: auto; height: auto; padding: 8px 14px; border-radius: 999px;
    font-size: 13px; background: rgba(15,23,42,.88); border: 1px solid rgba(255,255,255,.22); display: none; }
  body.problem #fallback { display: block; }
  #problemMsg { position: absolute; left: 50%; bottom: 22px; transform: translateX(-50%); font-size: 13px; padding: 7px 14px;
    border-radius: 10px; background: rgba(15,23,42,.88); display: none; white-space: nowrap; }
  body.problem:not(.has-video) #problemMsg { display: block; }
</style>
</head>
<body class="show-ui">
<div id="glow"></div>
<div id="stage">
  <iframe id="frame" allow="autoplay; fullscreen; encrypted-media; picture-in-picture; clipboard-write"
          allowfullscreen referrerpolicy="strict-origin-when-cross-origin"></iframe>
  <div id="loading"><div class="spinner"></div></div>
  <div id="shield"></div>
  <div id="flash"></div>
  <div id="bar">
    <div id="seekWrap"><div id="seekTrack"><div id="seekFill"></div><div id="seekKnob"></div></div><div id="seekTip">0:00</div></div>
    <div class="row">
      <button id="btnPlay" title="再生 / 一時停止 (Space)"></button>
      <div id="volWrap">
        <button id="btnMute" title="ミュート (M)"></button>
        <input id="vol" type="range" min="0" max="1" step="0.01" value="1">
      </div>
      <span id="time">0:00 / 0:00</span>
      <span id="liveTag">LIVE</span>
      <span class="spacer"></span>
      <button id="btnFs" title="全画面 (F)"></button>
    </div>
  </div>
  <button id="fallback">サイトで開く ↗</button>
  <div id="problemMsg">この動画は埋め込み再生できないようです。右上の「サイトで開く」で再生できます。</div>
</div>
<script>
(() => {
  const qs = new URLSearchParams(location.search);
  const src = qs.get('src') || '';
  const fallback = qs.get('fallback') || '';
  const site = qs.get('site') || '';
  const $ = (id) => document.getElementById(id);
  const frame = $('frame'), body = document.body;

  const ICON = {
    play: '<svg viewBox="0 0 24 24"><path d="M8 5.14v13.72a1 1 0 0 0 1.5.86l11-6.86a1 1 0 0 0 0-1.72l-11-6.86A1 1 0 0 0 8 5.14z"/></svg>',
    pause: '<svg viewBox="0 0 24 24"><path d="M7 5h3.5v14H7zM13.5 5H17v14h-3.5z"/></svg>',
    vol: '<svg viewBox="0 0 24 24"><path d="M4 9v6h4l5 5V4L8 9H4zm12.5 3a4.5 4.5 0 0 0-2.5-4.03v8.05A4.5 4.5 0 0 0 16.5 12zM14 3.23v2.06a7 7 0 0 1 0 13.42v2.06a9 9 0 0 0 0-17.54z"/></svg>',
    mute: '<svg viewBox="0 0 24 24"><path d="M4 9v6h4l5 5V4L8 9H4zm15.59 3 2.7-2.7-1.42-1.41L18.17 10.6l-2.7-2.71-1.41 1.41 2.7 2.7-2.7 2.71 1.41 1.41 2.7-2.7 2.7 2.7 1.42-1.41z"/></svg>',
    fs: '<svg viewBox="0 0 24 24"><path d="M5 5h5v2H7v3H5V5zm9 0h5v5h-2V7h-3V5zM5 14h2v3h3v2H5v-5zm12 3v-3h2v5h-5v-2h3z"/></svg>',
    fsExit: '<svg viewBox="0 0 24 24"><path d="M8 5h2v5H5V8h3V5zm6 0h2v3h3v2h-5V5zM5 14h5v5H8v-3H5v-2zm9 0h5v2h-3v3h-2v-5z"/></svg>'
  };

  const toHost = (msg) => { try { window.chrome.webview.postMessage(msg); } catch {} };

  // ---- アプリ (C#) からの命令を、埋め込みプレイヤー側へ中継する ----
  // 設定系は覚えておき、埋め込み側の準備ができた時（読み込み直後など）にもう一度送る
  const SETTERS = ['setZenTubeMode', 'setZenTubeAmbilight', 'setZenTubeDanmaku', 'loadZenTubeDanmakuPool',
    'setZenTubeAdBlock', 'setZenTubeBlockShorts', 'setZenTubePlaybackRate', 'setZenTubeLoop',
    'setZenTubeVideoFx', 'setZenTubeAudioProfile'];
  const ACTIONS = ['stepZenTubeABRepeat', 'clearZenTubeABRepeat', 'pauseZenTubeVideo', 'toggleZenTubePlayPause'];
  const cache = new Map();
  let activeWin = null;   // 動画を持っている埋め込みフレーム

  const post = (win, m) => { try { win && win.postMessage(m, '*'); } catch {} };
  const sendCall = (name, args) => post(frame.contentWindow, { __prismil: 'call', name, args });

  for (const n of SETTERS) {
    window[n] = (...args) => {
      cache.set(n, args);
      if (n === 'setZenTubeAmbilight') setAmbient(args[0]);
      sendCall(n, args);
    };
  }
  for (const n of ACTIONS) {
    window[n] = (...args) => post(activeWin || frame.contentWindow, { __prismil: 'call', name: n, args });
  }
  const cmd = (op, value) => post(activeWin || frame.contentWindow, { __prismil: 'cmd', op, value });
  // ---- 状態 ----
  let st = { t: 0, d: 0, paused: true, vol: 1, muted: false };
  let hasVideo = false;
  let seeking = false;

  window.addEventListener('message', (e) => {
    const d = e.data;
    if (!d || typeof d !== 'object' || !d.__prismil) return;
    switch (d.__prismil) {
      case 'ready':
        for (const [n, a] of cache) post(e.source, { __prismil: 'call', name: n, args: a });
        break;
      case 'host':
        if (typeof d.msg !== 'string') return;
        if (d.msg.startsWith('ambient_rgb3:')) applyGlow(d.msg.slice(13));
        toHost(d.msg);
        break;
      case 'state':
        if (d.error) { setProblem(true); hasVideo = false; body.classList.remove('has-video'); return; }
        if (!d.hasVideo) return;
        activeWin = e.source;
        st = d;
        if (!hasVideo) { hasVideo = true; body.classList.add('has-video'); setProblem(false); }
        render();
        break;
    }
  });

  // ---- 表示 ----
  const fmt = (s) => {
    if (!isFinite(s) || s < 0) s = 0;
    s = Math.floor(s);
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
    const pad = (n) => (n < 10 ? '0' + n : '' + n);
    return h > 0 ? h + ':' + pad(m) + ':' + pad(r) : m + ':' + pad(r);
  };
  const isLive = () => !isFinite(st.d);

  function render() {
    $('btnPlay').innerHTML = st.paused ? ICON.play : ICON.pause;
    $('btnMute').innerHTML = (st.muted || st.vol === 0) ? ICON.mute : ICON.vol;
    if (document.activeElement !== $('vol')) $('vol').value = st.muted ? 0 : st.vol;
    body.classList.toggle('live', isLive());
    if (!seeking && isFinite(st.d) && st.d > 0) setSeekVisual(st.t / st.d);
    $('time').textContent = fmt(st.t) + ' / ' + fmt(st.d);
    if (st.paused) showUi(true);
    else if (wasPaused) showUi();
    wasPaused = st.paused;
  }
  let wasPaused = true;
  function setSeekVisual(ratio) {
    ratio = Math.max(0, Math.min(1, ratio || 0));
    $('seekFill').style.width = (ratio * 100) + '%';
    $('seekKnob').style.left = (ratio * 100) + '%';
  }

  function setProblem(on) { body.classList.toggle('problem', on && !!fallback); }
  $('fallback').innerHTML = 'サイトで開く ↗';
  $('fallback').addEventListener('click', () => { if (fallback) location.href = fallback; });

  // 一定時間たっても動画が見つからない時は「サイトで開く」を出す（SoundCloud は音声だけなので除く）
  if (site !== 'soundcloud') setTimeout(() => { if (!hasVideo) setProblem(true); }, 12000);
  else $('loading').style.display = 'none';

  // ---- アンビエントライト（動画の色で背景を光らせる） ----
  let ambiMode = 'vivid';
  function setAmbient(mode) {
    ambiMode = mode || 'off';
    body.classList.toggle('ambi', ambiMode !== 'off');
    body.classList.toggle('ambi-soft', ambiMode === 'soft');
  }
  function applyGlow(s) {
    if (ambiMode === 'off') return;
    const c = s.split(';');
    if (c.length !== 3) return;
    const a = ambiMode === 'ultra' ? 0.95 : 0.75;
    $('glow').style.background =
      `radial-gradient(55% 85% at 0% 50%, rgba(${c[0]},${a}), transparent 72%),` +
      `radial-gradient(70% 75% at 50% 50%, rgba(${c[1]},${a * 0.8}), transparent 78%),` +
      `radial-gradient(55% 85% at 100% 50%, rgba(${c[2]},${a}), transparent 72%), #040508`;
  }

  // ---- 操作 ----
  let flashTimer = 0;
  function flash(icon) {
    const f = $('flash');
    f.innerHTML = icon;
    f.classList.remove('on'); void f.offsetWidth; f.classList.add('on');
    clearTimeout(flashTimer); flashTimer = setTimeout(() => f.classList.remove('on'), 600);
  }
  function togglePlay() { flash(st.paused ? ICON.play : ICON.pause); st.paused = !st.paused; render(); cmd('toggle'); }
  function toggleFs() {
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else document.documentElement.requestFullscreen().catch(() => {});
  }
  document.addEventListener('fullscreenchange', () => {
    body.classList.toggle('fs', !!document.fullscreenElement);
    $('btnFs').innerHTML = document.fullscreenElement ? ICON.fsExit : ICON.fs;
  });

  $('btnPlay').addEventListener('click', togglePlay);
  $('btnMute').addEventListener('click', () => cmd('toggleMute'));
  $('vol').addEventListener('input', (e) => cmd('volume', Number(e.target.value)));
  $('btnFs').addEventListener('click', toggleFs);
  $('btnFs').innerHTML = ICON.fs;
  $('btnPlay').innerHTML = ICON.play;
  $('btnMute').innerHTML = ICON.vol;

  // 1回クリック＝再生/一時停止、ダブルクリック＝全画面
  let clickTimer = 0;
  $('shield').addEventListener('click', () => {
    clearTimeout(clickTimer);
    clickTimer = setTimeout(togglePlay, 220);
  });
  $('shield').addEventListener('dblclick', () => { clearTimeout(clickTimer); toggleFs(); });
  $('shield').addEventListener('wheel', (e) => {
    e.preventDefault();
    const v = Math.max(0, Math.min(1, (st.muted ? 0 : st.vol) + (e.deltaY < 0 ? 0.05 : -0.05)));
    st.vol = v; st.muted = false; render(); cmd('volume', v);
  }, { passive: false });

  // シークバー
  const seekWrap = $('seekWrap');
  const ratioAt = (x) => { const r = seekWrap.getBoundingClientRect(); return Math.max(0, Math.min(1, (x - r.left) / r.width)); };
  seekWrap.addEventListener('mousemove', (e) => {
    const ratio = ratioAt(e.clientX);
    const tip = $('seekTip');
    tip.textContent = fmt(ratio * st.d);
    tip.style.left = (ratio * 100) + '%';
    if (seeking) setSeekVisual(ratio);
  });
  seekWrap.addEventListener('mousedown', (e) => {
    if (!isFinite(st.d) || st.d <= 0) return;
    seeking = true; seekWrap.classList.add('drag'); setSeekVisual(ratioAt(e.clientX));
    const move = (ev) => { setSeekVisual(ratioAt(ev.clientX)); const tip = $('seekTip'); const r = ratioAt(ev.clientX); tip.textContent = fmt(r * st.d); tip.style.left = (r * 100) + '%'; };
    const up = (ev) => {
      document.removeEventListener('mousemove', move); document.removeEventListener('mouseup', up);
      const t = ratioAt(ev.clientX) * st.d;
      st.t = t; cmd('seek', t);
      seekWrap.classList.remove('drag');
      setTimeout(() => { seeking = false; }, 400);
    };
    document.addEventListener('mousemove', move); document.addEventListener('mouseup', up);
  });

  // キーボード
  document.addEventListener('keydown', (e) => {
    if (!hasVideo || e.ctrlKey || e.altKey || e.metaKey) return;
    const k = e.key.toLowerCase();
    if (k === ' ' || k === 'k') { togglePlay(); }
    else if (k === 'arrowleft' || k === 'j') { cmd('seekBy', k === 'j' ? -10 : -5); }
    else if (k === 'arrowright' || k === 'l') { cmd('seekBy', k === 'l' ? 10 : 5); }
    else if (k === 'arrowup') { cmd('volume', Math.min(1, (st.muted ? 0 : st.vol) + 0.05)); }
    else if (k === 'arrowdown') { cmd('volume', Math.max(0, (st.muted ? 0 : st.vol) - 0.05)); }
    else if (k === 'm') { cmd('toggleMute'); }
    else if (k === 'f') { toggleFs(); }
    else if (k >= '0' && k <= '9' && isFinite(st.d) && st.d > 0) { cmd('seek', st.d * Number(k) / 10); }
    else return;
    e.preventDefault();
    showUi();
  });

  // 操作バーはマウスを動かしている間だけ表示
  let idleTimer = 0;
  function showUi(keep) {
    body.classList.add('show-ui'); body.classList.remove('idle');
    clearTimeout(idleTimer);
    if (keep) return;
    idleTimer = setTimeout(() => {
      if (st.paused || seeking || $('bar').matches(':hover')) return;
      body.classList.remove('show-ui'); body.classList.add('idle');
    }, 2600);
  }
  document.addEventListener('mousemove', () => showUi());
  document.addEventListener('mouseleave', () => { if (!st.paused) { body.classList.remove('show-ui'); } });

  setAmbient('vivid');
  if (src.startsWith('https://')) frame.src = src;
  showUi();
})();
</script>
</body>
</html>
""";
}
