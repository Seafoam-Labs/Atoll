// Keyboard model for the tablist in InstallCmdBlock.razor: ArrowLeft/ArrowRight wrap, Home/End
// jump. Activation is automatic, so the moved trigger is clicked and the selection routes through
// the component's @onclick like a mouse click. The roving tabindex (selected trigger 0, the rest
// -1) is rendered by the component and Blazor rewrites it when the click's re-render lands.
export function initTabKeys(el) {
    el.addEventListener('keydown', (event) => {
        const moves = { ArrowLeft: -1, ArrowRight: 1, Home: 'first', End: 'last' };
        const move = moves[event.key];
        if (move === undefined) {
            return;
        }
        const tabs = [...el.querySelectorAll('[role="tab"]:not([disabled])')];
        const current = tabs.indexOf(document.activeElement);
        if (current < 0) {
            return;
        }
        event.preventDefault();
        const next = move === 'first' ? 0
            : move === 'last' ? tabs.length - 1
                : (current + move + tabs.length) % tabs.length;
        tabs[next].focus();
        tabs[next].click();
    });
}
