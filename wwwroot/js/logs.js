(function () {
    const statusEl = document.getElementById('logs-status');
    const listEl = document.getElementById('logs-list');
    const refreshBtn = document.getElementById('btn-refresh-logs');
    const clearBtn = document.getElementById('btn-clear-logs');

    if (!statusEl || !listEl) return;

    let loaded = false;
    let loading = false;

    function formatDate(value) {
        if (!value) return '';
        try {
            const date = new Date(value);
            if (Number.isNaN(date.getTime())) return String(value);
            return date.toLocaleString();
        } catch (err) {
            console.error('Failed to format date', value, err);
            return String(value);
        }
    }

    function levelClass(level) {
        const normalized = (level || '').toString().toLowerCase();
        switch (normalized) {
            case 'success':
                return 'log-level log-level-success';
            case 'warning':
                return 'log-level log-level-warning';
            case 'error':
                return 'log-level log-level-error';
            default:
                return 'log-level log-level-info';
        }
    }

    function render(entries) {
        listEl.innerHTML = '';
        if (!Array.isArray(entries) || entries.length === 0) {
            statusEl.textContent = 'No activity yet.';
            return;
        }

        const table = document.createElement('div');
        table.className = 'log-table';

        const header = document.createElement('div');
        header.className = 'log-row log-row-head';
        header.innerHTML = `
            <div class="log-cell log-col-time">Time</div>
            <div class="log-cell log-col-source">Source</div>
            <div class="log-cell log-col-level">Level</div>
            <div class="log-cell log-col-message">Message</div>
            <div class="log-cell log-col-details">Details</div>
        `;
        table.appendChild(header);

        entries.forEach((entry, index) => {
            const row = document.createElement('div');
            row.className = 'log-row';

            const timeCell = document.createElement('div');
            timeCell.className = 'log-cell log-col-time';
            timeCell.textContent = formatDate(entry.createdAt);

            const sourceCell = document.createElement('div');
            sourceCell.className = 'log-cell log-col-source';
            sourceCell.textContent = entry.source || 'system';

            const levelCell = document.createElement('div');
            levelCell.className = 'log-cell log-col-level';
            const badge = document.createElement('span');
            badge.className = levelClass(entry.level);
            badge.textContent = (entry.level || 'info').toString().toUpperCase();
            levelCell.appendChild(badge);

            const messageCell = document.createElement('div');
            messageCell.className = 'log-cell log-col-message';
            messageCell.textContent = entry.message || '—';

            const detailsCell = document.createElement('div');
            detailsCell.className = 'log-cell log-col-details';
            const hasDetails = entry.details !== undefined && entry.details !== null;
            if (hasDetails) {
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'btn btn-ghost btn-small';
                btn.textContent = 'View';
                btn.addEventListener('click', () => {
                    detailsRow.classList.toggle('hidden');
                });
                detailsCell.appendChild(btn);
            } else {
                detailsCell.textContent = '—';
            }

            row.appendChild(timeCell);
            row.appendChild(sourceCell);
            row.appendChild(levelCell);
            row.appendChild(messageCell);
            row.appendChild(detailsCell);
            table.appendChild(row);

            const detailsRow = document.createElement('div');
            detailsRow.className = 'log-row log-details-row hidden';
            const detailsCellFull = document.createElement('div');
            detailsCellFull.className = 'log-cell log-col-details-full';
            detailsCellFull.setAttribute('colspan', '5');

            if (hasDetails) {
                const pre = document.createElement('pre');
                pre.className = 'log-details';
                try {
                    const text = typeof entry.details === 'string'
                        ? entry.details
                        : JSON.stringify(entry.details, null, 2);
                    pre.textContent = text || '';
                } catch (err) {
                    pre.textContent = 'Unable to display details.';
                    console.error('Failed to render log details', err);
                }
                detailsCellFull.appendChild(pre);
            } else {
                detailsCellFull.textContent = 'No details.';
            }

            detailsRow.appendChild(detailsCellFull);
            table.appendChild(detailsRow);
        });

        listEl.appendChild(table);
        statusEl.textContent = `Showing ${entries.length} recent event(s).`;
    }

    async function loadLogs(force = false) {
        if (loading) return;
        if (loaded && !force) return;

        loading = true;
        statusEl.textContent = 'Loading activity…';
        try {
            const res = await fetch('/logs?take=200');
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();
            render(data?.entries || []);
            loaded = true;
        } catch (err) {
            statusEl.textContent = 'Unable to load activity log.';
            console.error('Failed to load activity log', err);
        } finally {
            loading = false;
        }
    }

    async function clearLogs() {
        if (loading) return;
        const confirmClear = window.confirm('Clear the activity log?');
        if (!confirmClear) return;

        loading = true;
        statusEl.textContent = 'Clearing activity…';
        try {
            const res = await fetch('/logs', { method: 'DELETE' });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            listEl.innerHTML = '';
            statusEl.textContent = 'Activity log cleared.';
            loaded = false;
        } catch (err) {
            statusEl.textContent = 'Unable to clear activity log.';
            console.error('Failed to clear activity log', err);
        } finally {
            loading = false;
        }
    }

    if (refreshBtn) {
        refreshBtn.addEventListener('click', () => loadLogs(true));
    }
    if (clearBtn) {
        clearBtn.addEventListener('click', clearLogs);
    }

    window.bookwormLogs = {
        ensureLoaded: () => loadLogs(false),
        reload: () => loadLogs(true)
    };
})();
