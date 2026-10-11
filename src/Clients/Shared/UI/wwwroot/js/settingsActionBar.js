let current = null;
let observer = null;

function disconnect() {
    if (observer) {
        observer.disconnect();
        observer = null;
    }

    current = null;
}

export function observe(element, variableName) {
    if (!element)
        return;

    disconnect();
    current = element;

    const publish = () => {
        if (current !== element)
            return;

        const height = Math.ceil(element.getBoundingClientRect().height);
        document.documentElement.style.setProperty(variableName, `${height}px`);
    };

    publish();
    observer = new ResizeObserver(publish);
    observer.observe(element);
}

export function unobserve(element, variableName) {
    if (current !== element)
        return;

    disconnect();
    document.documentElement.style.removeProperty(variableName);
}
