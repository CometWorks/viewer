const handlers = new WeakMap();
export function open(dialog, receiver) {
    const cancel = event => {
        event.preventDefault();
        receiver.invokeMethodAsync("CloseAsync").catch(() => close(dialog));
    };
    handlers.set(dialog, cancel);
    dialog.addEventListener("cancel", cancel);
    dialog.showModal();
}
export function close(dialog) {
    if (dialog.open) dialog.close();
}
export function dispose(dialog) {
    close(dialog);
    const cancel = handlers.get(dialog);
    if (cancel) dialog.removeEventListener("cancel", cancel);
    handlers.delete(dialog);
}
