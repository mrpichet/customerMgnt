// เข้าสู่ระบบสมาชิกด้วย Google (Google Identity Services) และ Facebook (JS SDK)
// ได้ token ของ provider แล้วส่งกลับให้ Blazor (.NET) ไปแลกเป็น token ของ AuthServer — ไม่เก็บ token ไว้ที่นี่
(function () {
    'use strict';
    const loaded = {};

    function loadScript(src) {
        if (loaded[src]) { return loaded[src]; }
        loaded[src] = new Promise(function (resolve, reject) {
            const s = document.createElement('script');
            s.src = src;
            s.async = true;
            s.defer = true;
            s.onload = function () { resolve(); };
            s.onerror = function () { delete loaded[src]; reject(new Error('load failed: ' + src)); };
            document.head.appendChild(s);
        });
        return loaded[src];
    }

    window.membershipExternalLogin = {
        // วาดปุ่ม Google ใน element ที่ระบุ; เมื่อผู้ใช้เลือกบัญชีจะเรียก OnProviderToken('Google', id_token)
        renderGoogle: async function (clientId, elementId, dotnetRef) {
            await loadScript('https://accounts.google.com/gsi/client');
            window.google.accounts.id.initialize({
                client_id: clientId,
                callback: function (resp) { dotnetRef.invokeMethodAsync('OnProviderToken', 'Google', resp.credential); },
                ux_mode: 'popup',
                auto_select: false
            });
            const el = document.getElementById(elementId);
            if (el) {
                window.google.accounts.id.renderButton(el, { theme: 'outline', size: 'large', text: 'signin_with', shape: 'rectangular', locale: 'th', width: 280 });
            }
        },

        // เปิดหน้าต่าง Facebook Login แล้วคืน access token (หรือ null เมื่อผู้ใช้ยกเลิก)
        loginFacebook: async function (appId) {
            await loadScript('https://connect.facebook.net/th_TH/sdk.js');
            if (!window.__mbFbInit) {
                window.FB.init({ appId: appId, cookie: false, xfbml: false, version: 'v19.0' });
                window.__mbFbInit = true;
            }
            return await new Promise(function (resolve) {
                window.FB.login(function (resp) {
                    resolve(resp && resp.authResponse ? resp.authResponse.accessToken : null);
                }, { scope: 'public_profile,email' });
            });
        },

        // ออกจาก Google auto-select เมื่อ logout
        signOut: function () {
            if (window.google && window.google.accounts && window.google.accounts.id) { window.google.accounts.id.disableAutoSelect(); }
        }
    };
})();
