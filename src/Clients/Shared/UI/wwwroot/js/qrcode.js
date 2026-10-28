window.k7QrCode = {
    generate: function (elementId, text, size) {
        var run = function () {
            var el = document.getElementById(elementId);
            if (!el || !text) return;
            var s = size || 200;
            el.innerHTML = '';
            new QRCode(el, {
                text: text,
                width: s,
                height: s,
                colorDark: '#000000',
                colorLight: '#ffffff',
                correctLevel: QRCode.CorrectLevel.M
            });
        };

        if (typeof QRCode === 'function') {
            run();
            return;
        }

        if (window.K7 && typeof K7.ensureQrCode === 'function')
            return K7.ensureQrCode().then(run);

        run();
    }
};
