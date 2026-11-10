export function createAsyncQueue(limit) {
    let active = 0;
    const queued = [];

    function runNext() {
        while (active < limit && queued.length) {
            const item = queued.shift();
            active++;
            Promise.resolve()
                .then(item.operation)
                .then(item.resolve, item.reject)
                .finally(() => {
                    active--;
                    runNext();
                });
        }
    }

    return operation => new Promise((resolve, reject) => {
        queued.push({ operation, resolve, reject });
        runNext();
    });
}
