(function() {
    window.__louisvox_suppress_blur = false;
    window.__louisvox_network_unstable = false;
    window.__exambro_overlay_active = false;
    window.__cbt_lightbox_active = false;
    window.__cbt_last_unstable_time = 0;

    // Pantau event status koneksi browser untuk toleransi tinggi
    window.addEventListener('offline', function() {
        window.__louisvox_network_unstable = true;
        window.__louisvox_suppress_blur = true;
        window.__cbt_last_unstable_time = Date.now();
    });
    window.addEventListener('online', function() {
        window.__cbt_last_unstable_time = Date.now();
        setTimeout(function() {
            if (typeof navigator !== 'undefined' && navigator.onLine && !window.__louisvox_network_unstable) {
                window.__louisvox_suppress_blur = false;
            }
        }, 15000);
    });

    // Multi-layer DOM capture to completely disable F10, Ctrl+C, Ctrl+V, Ctrl+X, Insert, and clipboard operations
    window.addEventListener('keydown', function(e) {
        if (e.key === 'F10' || e.keyCode === 121 || (e.keyCode >= 112 && e.keyCode <= 135)) {
            e.preventDefault();
            e.stopPropagation();
            return false;
        }
        if ((e.ctrlKey || e.metaKey) && (e.key === 'c' || e.key === 'C' || e.key === 'v' || e.key === 'V' || e.key === 'x' || e.key === 'X' || e.keyCode === 67 || e.keyCode === 86 || e.keyCode === 88 || e.keyCode === 45)) {
            e.preventDefault();
            e.stopPropagation();
            return false;
        }
        if (e.shiftKey && (e.key === 'Insert' || e.keyCode === 45)) {
            e.preventDefault();
            e.stopPropagation();
            return false;
        }
    }, true);
    window.addEventListener('keyup', function(e) {
        if (e.key === 'F10' || e.keyCode === 121 || (e.keyCode >= 112 && e.keyCode <= 135)) {
            e.preventDefault();
            e.stopPropagation();
            return false;
        }
        if ((e.ctrlKey || e.metaKey) && (e.key === 'c' || e.key === 'C' || e.key === 'v' || e.key === 'V' || e.key === 'x' || e.key === 'X' || e.keyCode === 67 || e.keyCode === 86 || e.keyCode === 88 || e.keyCode === 45)) {
            e.preventDefault();
            e.stopPropagation();
            return false;
        }
        if (e.shiftKey && (e.key === 'Insert' || e.keyCode === 45)) {
            e.preventDefault();
            e.stopPropagation();
            return false;
        }
    }, true);
    window.addEventListener('copy', function(e) {
        e.preventDefault();
        e.stopPropagation();
        return false;
    }, true);
    window.addEventListener('cut', function(e) {
        e.preventDefault();
        e.stopPropagation();
        return false;
    }, true);
    window.addEventListener('paste', function(e) {
        e.preventDefault();
        e.stopPropagation();
        return false;
    }, true);

    try {
        const origGetAttr = Element.prototype.getAttribute;
        Element.prototype.getAttribute = function(name) {
            if (name === 'data-is-exambrowser' && this && this.id === 'cbt-anti-cheat-root') {
                return '1';
            }
            return origGetAttr.apply(this, arguments);
        };
    } catch(e) {}

    try {
        const fsElemGetter = {
            get: function() { return document.documentElement; },
            configurable: true
        };
        Object.defineProperty(document, 'fullscreenElement', fsElemGetter);
        Object.defineProperty(document, 'webkitFullscreenElement', fsElemGetter);
        Object.defineProperty(document, 'mozFullScreenElement', fsElemGetter);
    } catch(e) {}

    try {
        const resolveFs = function() {
            try {
                const evt = new Event('fullscreenchange');
                document.dispatchEvent(evt);
            } catch(e) {}
            return Promise.resolve();
        };
        Element.prototype.requestFullscreen = resolveFs;
        Element.prototype.webkitRequestFullscreen = resolveFs;
        Element.prototype.mozRequestFullScreen = resolveFs;
        Element.prototype.msRequestFullscreen = resolveFs;
    } catch(e) {}

    function dismissFullscreenGate() {
        const root = document.getElementById('cbt-anti-cheat-root');
        if (root) {
            root.setAttribute('data-is-exambrowser', '1');
        }
        const gate = document.getElementById('anti-cheat-fullscreen-gate');
        if (gate) {
            gate.classList.add('hidden');
            gate.classList.remove('flex');
            gate.style.display = 'none';
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', dismissFullscreenGate);
    } else {
        dismissFullscreenGate();
    }
    setInterval(dismissFullscreenGate, 500);

    // Intercept fetch untuk toleransi jaringan putus sementara dan auto-retry penyimpanan jawaban
    try {
        const origFetch = window.fetch;
        window.fetch = async function(input, init) {
            const url = typeof input === 'string' ? input : (input && input.url ? input.url : '');

            // 1. Intercept pelaporan pelanggaran saat jaringan tidak stabil atau aksi salin/tempel tanpa pelanggaran
            if (url.includes('/violation')) {
                try {
                    if (init && init.body && typeof init.body === 'string') {
                        const parsed = JSON.parse(init.body);
                        const vt = parsed.violation_type || parsed.type || '';
                        if (vt === 'copy' || vt === 'paste' || vt === 'cut' || vt === 'clipboard') {
                            return new Response(JSON.stringify({
                                action: 'ignored',
                                message: 'Kombinasi Ctrl+C / Ctrl+V dinonaktifkan tanpa pelanggaran.',
                                is_locked: false
                            }), {
                                status: 200,
                                headers: { 'Content-Type': 'application/json' }
                            });
                        }
                    }
                } catch(e) {}

                const now = Date.now();
                const isUnstable = (typeof navigator !== 'undefined' && !navigator.onLine) ||
                                   window.__louisvox_suppress_blur === true ||
                                   window.__louisvox_network_unstable === true ||
                                   (now - (window.__cbt_last_unstable_time || 0) < 15000);

                if (isUnstable) {
                    let isSensitive = true;
                    try {
                        if (init && init.body) {
                            const parsed = JSON.parse(init.body);
                            const vt = parsed.violation_type;
                            if (vt === 'severe_unpin' || parsed.metadata?.is_unpin) {
                                isSensitive = false;
                            }
                        }
                    } catch(e) {}

                    if (isSensitive) {
                        return new Response(JSON.stringify({
                            action: 'ignored',
                            message: 'Toleransi jaringan tidak stabil / putus sementara aktif.',
                            is_locked: false
                        }), {
                            status: 200,
                            headers: { 'Content-Type': 'application/json' }
                        });
                    }
                }
            }

            // 2. Proteksi penyimpanan jawaban: auto-retry jika gagal karena gangguan koneksi
            if (url.includes('/answer')) {
                try {
                    return await origFetch.apply(this, arguments);
                } catch (err) {
                    window.__louisvox_network_unstable = true;
                    window.__cbt_last_unstable_time = Date.now();
                    const retryArgs = arguments;
                    setTimeout(async () => {
                        for (let attempt = 0; attempt < 5; attempt++) {
                            try {
                                await new Promise(r => setTimeout(r, 1000 * Math.pow(2, attempt)));
                                await origFetch.apply(window, retryArgs);
                                break;
                            } catch(re) {}
                        }
                    }, 1000);
                    throw err;
                }
            }

            return origFetch.apply(this, arguments);
        };
    } catch(e) {}

    window.LouisVoxExamApp = {
        onWebExamConfig: function(maxStrikes, strikes, isLocked) {
            try {
                window.chrome.webview.postMessage(JSON.stringify({
                    handler: 'CBT_CONFIG',
                    maxStrikes: maxStrikes,
                    strikes: strikes,
                    isLocked: isLocked
                }));
            } catch (e) {}
        },
        onWebViolationRecorded: function(type, strikes, maxStrikes, message, isLocked) {
            try {
                window.chrome.webview.postMessage(JSON.stringify({
                    handler: 'CBT_VIOLATION',
                    type: type,
                    strikes: strikes,
                    maxStrikes: maxStrikes,
                    message: message,
                    isLocked: isLocked
                }));
            } catch (e) {}
        },
        isAlarmOverlayActive: function() {
            const now = Date.now();
            const isRecentUnstable = (now - (window.__cbt_last_unstable_time || 0)) < 15000;
            const isOffline = typeof navigator !== 'undefined' && !navigator.onLine;
            return window.__exambro_overlay_active === true ||
                   window.__cbt_lightbox_active === true ||
                   window.__louisvox_suppress_blur === true ||
                   window.__louisvox_network_unstable === true ||
                   isRecentUnstable ||
                   isOffline;
        },
        setLightboxActive: function(active) {
            window.__cbt_lightbox_active = !!active;
        },
        onWebSessionUnlocked: function(strikes) {
            try {
                window.chrome.webview.postMessage(JSON.stringify({
                    handler: 'CBT_UNLOCKED',
                    strikes: strikes
                }));
            } catch (e) {}
        },
        onExamFinished: function() {
            try {
                window.chrome.webview.postMessage(JSON.stringify({
                    handler: 'CBT_FINISHED'
                }));
            } catch (e) {}
        }
    };

    try {
        window.close = function() {
            try {
                window.chrome.webview.postMessage(JSON.stringify({
                    handler: 'CBT_FINISHED'
                }));
            } catch (e) {}
        };
    } catch (e) {}
})();
