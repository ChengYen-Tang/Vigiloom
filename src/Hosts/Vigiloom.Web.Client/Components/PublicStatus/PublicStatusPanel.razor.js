export function create(receiver) {
    let active;
    let latestGeneration = -1;

    function closeCurrent() {
        if (!active) return;
        active.source?.close();
        if (active.retry !== null) clearTimeout(active.retry);
        active = undefined;
    }

    function stop(generation) {
        latestGeneration = Math.max(latestGeneration, generation + 1);
        if (active?.generation === generation) closeCurrent();
    }

    function connect(entry) {
        if (active !== entry) return;
        const source = new EventSource(`/api/v1/public/${encodeURIComponent(entry.kind)}/${encodeURIComponent(entry.id)}/events`);
        entry.source = source;
        source.addEventListener("status", event => {
            if (active === entry) receiver.invokeMethodAsync("OnPublicSubscription", entry.generation, "status", event.data).catch(() => stop(entry.generation));
        });
        source.addEventListener("unavailable", () => {
            if (active !== entry) return;
            stop(entry.generation);
            receiver.invokeMethodAsync("OnPublicSubscription", entry.generation, "unavailable", null).catch(() => {});
        });
        source.onerror = () => {
            if (active !== entry) return;
            source.close();
            receiver.invokeMethodAsync("OnPublicSubscription", entry.generation, "disconnected", null).catch(() => stop(entry.generation));
            entry.retry = setTimeout(() => connect(entry), 5000);
        };
    }

    return {
        start(kind, id, generation) {
            if (generation < latestGeneration) return;
            latestGeneration = generation;
            closeCurrent();
            active = { kind, id, generation, source: null, retry: null };
            connect(active);
        },
        stop,
        dispose() { closeCurrent(); }
    };
}
