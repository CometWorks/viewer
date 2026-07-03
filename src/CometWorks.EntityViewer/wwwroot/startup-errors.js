(function () {
    function showStartupError(message) {
        const overlay = document.getElementById("loadingOverlay");
        const title = document.getElementById("loadingTitle");
        const text = document.getElementById("loadingText");
        const progress = document.getElementById("loadingProgress");
        if (!overlay || !title || !text) return;

        overlay.classList.remove("is-hidden");
        overlay.classList.add("is-error");
        title.textContent = "Could not start viewer";
        text.textContent = message || "Viewer startup failed.";
        if (progress) {
            progress.classList.remove("is-indeterminate");
            progress.style.width = "100%";
        }
    }

    window.addEventListener("error", event => {
        const target = event.target;
        if (target && target !== window) {
            const tagName = String(target.tagName || "").toLowerCase();
            if (tagName === "script" || tagName === "link") {
                showStartupError(`Could not load ${tagName}: ${target.src || target.href || "unknown asset"}`);
                return;
            }
        }

        if (event.message) showStartupError(event.message);
    }, true);

    window.addEventListener("unhandledrejection", event => {
        const reason = event.reason;
        showStartupError(reason && reason.message ? reason.message : String(reason || "Unhandled startup error."));
    });
})();
