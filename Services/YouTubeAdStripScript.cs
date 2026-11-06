namespace ZenTube.Services;

/// <summary>
/// YouTube の広告を「再生が始まる前」に消すスクリプト。
/// プレイヤーが読み込む再生情報（ytInitialPlayerResponse や /youtubei/v1/player の応答）から
/// 広告の予定（adPlacements / playerAds / adSlots など）を抜き取るので、広告そのものが始まらない。
/// ページのスクリプトより先に動く必要があるため、AddScriptToExecuteOnDocumentCreated で登録する。
/// </summary>
public static class YouTubeAdStripScript
{
    public const string Script = """
        (function() {
            if (!/(^|\.)youtube\.com$/.test(location.hostname)) return;
            if (window.__zentubeAdStripInstalled) return;
            window.__zentubeAdStripInstalled = true;

            const AD_KEYS = ['adPlacements', 'playerAds', 'adSlots', 'adBreakHeartbeatParams', 'adBreakParams'];
            let strippedCount = 0;

            // 再生情報から広告の項目を取り除く（見つかったら true）
            function strip(obj) {
                if (!obj || typeof obj !== 'object') return false;
                let hit = false;
                const targets = [obj, obj.playerResponse, obj.response, obj.data];
                if (Array.isArray(obj)) {
                    for (const item of obj) targets.push(item, item && item.playerResponse);
                }
                for (const t of targets) {
                    if (!t || typeof t !== 'object') continue;
                    for (const k of AD_KEYS) {
                        if (k in t) { try { delete t[k]; hit = true; } catch {} }
                    }
                }
                if (hit) {
                    strippedCount++;
                    try { window.chrome && window.chrome.webview && window.chrome.webview.postMessage('ad_skipped'); } catch {}
                }
                return hit;
            }

            // 1) ページに埋め込まれた最初の再生情報（var ytInitialPlayerResponse = {...}）
            let initialResponse;
            try {
                Object.defineProperty(window, 'ytInitialPlayerResponse', {
                    configurable: true,
                    get() { return initialResponse; },
                    set(v) { strip(v); initialResponse = v; }
                });
            } catch {}

            // 2) JSON.parse を通る再生情報（ページ内の移動や埋め込みプレイヤー）
            const origParse = JSON.parse;
            JSON.parse = function(text, reviver) {
                const result = origParse.call(this, text, reviver);
                try {
                    if (typeof text === 'string' && text.length > 200 &&
                        (text.includes('"adPlacements"') || text.includes('"playerAds"') || text.includes('"adSlots"'))) {
                        strip(result);
                    }
                } catch {}
                return result;
            };

            // 3) fetch の Response.json() で受け取る再生情報（/youtubei/v1/player, /next）
            const origJson = Response.prototype.json;
            Response.prototype.json = function() {
                const url = this.url || '';
                return origJson.call(this).then(data => {
                    if (url.includes('/youtubei/v1/player') || url.includes('/youtubei/v1/next')) {
                        try { strip(data); } catch {}
                    }
                    return data;
                });
            };

            // 4) 古い形式の XHR で受け取る再生情報
            const origOpen = XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open = function(method, url) {
                this.__zentubeUrl = String(url || '');
                return origOpen.apply(this, arguments);
            };
            const respDesc = Object.getOwnPropertyDescriptor(XMLHttpRequest.prototype, 'responseText');
            if (respDesc && respDesc.get) {
                Object.defineProperty(XMLHttpRequest.prototype, 'responseText', {
                    configurable: true,
                    get() {
                        const text = respDesc.get.call(this);
                        const url = this.__zentubeUrl || '';
                        if (typeof text === 'string' && url.includes('/youtubei/v1/player') && text.includes('"adPlacements"')) {
                            try {
                                const obj = origParse(text);
                                if (strip(obj)) return JSON.stringify(obj);
                            } catch {}
                        }
                        return text;
                    }
                });
            }

            window.__zentubeAdStripCount = () => strippedCount;
        })();
        """;
}
