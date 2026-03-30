/* global ApiClient, Dashboard */
define([], function () {
    'use strict';

    return function (view) {

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        function apiBase() {
            return ApiClient.serverAddress();
        }

        function apiFetch(path, options) {
            var url = apiBase() + path;
            var headers = {
                'X-Emby-Token': ApiClient.accessToken(),
                'Content-Type': 'application/json'
            };
            var opts = Object.assign({ headers: headers }, options || {});
            return fetch(url, opts).then(function (resp) {
                if (!resp.ok) {
                    return resp.text().catch(function () { return ''; }).then(function (text) {
                        throw new Error('HTTP ' + resp.status + ': ' + text);
                    });
                }
                var ct = resp.headers.get('content-type') || '';
                if (ct.indexOf('application/json') !== -1) return resp.json();
                return resp.text();
            });
        }

        function apiGet(path) { return apiFetch(path, { method: 'GET' }); }

        function apiPost(path, body) {
            return apiFetch(path, {
                method: 'POST',
                body: body !== undefined ? JSON.stringify(body) : undefined
            });
        }

        function apiPut(path, body) {
            return apiFetch(path, {
                method: 'PUT',
                body: body !== undefined ? JSON.stringify(body) : undefined
            });
        }

        function apiDelete(path) { return apiFetch(path, { method: 'DELETE' }); }

        function q(id) { return view.querySelector('#' + id); }

        function setStatus(el, msg, isError) {
            el.textContent = msg;
            el.style.color = isError
                ? 'var(--theme-error-color, #e53935)'
                : 'var(--theme-success-color, #43a047)';
        }

        function clearStatus(el) { el.textContent = ''; }

        // -------------------------------------------------------------------
        // Connector table
        // -------------------------------------------------------------------

        function renderConnectorTable(connectors) {
            var tbody = q('connectorTableBody');
            var table = q('connectorTable');
            var noMsg = q('noConnectorsMsg');

            while (tbody.firstChild) tbody.removeChild(tbody.firstChild);

            if (!connectors || connectors.length === 0) {
                table.style.display = 'none';
                noMsg.style.display = '';
                return;
            }

            table.style.display = '';
            noMsg.style.display = 'none';

            connectors.forEach(function (c) {
                var tr = document.createElement('tr');

                function td(text) {
                    var cell = document.createElement('td');
                    cell.style.cssText = 'padding:6px 8px';
                    cell.textContent = text;
                    return cell;
                }

                tr.appendChild(td(c.DisplayName || ''));
                tr.appendChild(td(c.ServerType || ''));
                tr.appendChild(td(c.ServerUrl || ''));

                var enabledCell = document.createElement('td');
                enabledCell.style.cssText = 'padding:6px 8px';
                enabledCell.textContent = c.Enabled ? '\u2713' : '\u2014';
                tr.appendChild(enabledCell);

                var actionCell = document.createElement('td');
                actionCell.style.cssText = 'padding:6px 8px';

                var btnEdit = document.createElement('button');
                btnEdit.setAttribute('is', 'emby-button');
                btnEdit.className = 'emby-button';
                btnEdit.textContent = 'Edit';
                btnEdit.style.marginRight = '4px';
                btnEdit.addEventListener('click', (function (id) {
                    return function () { openEditConnector(id); };
                }(c.Id)));

                var btnDelete = document.createElement('button');
                btnDelete.setAttribute('is', 'emby-button');
                btnDelete.className = 'emby-button';
                btnDelete.textContent = 'Delete';
                btnDelete.style.marginRight = '4px';
                btnDelete.addEventListener('click', (function (id) {
                    return function () { deleteConnector(id); };
                }(c.Id)));

                var btnSync = document.createElement('button');
                btnSync.setAttribute('is', 'emby-button');
                btnSync.className = 'emby-button';
                btnSync.textContent = 'Sync';
                btnSync.addEventListener('click', (function (id) {
                    return function () { syncSingleConnector(id); };
                }(c.Id)));

                actionCell.appendChild(btnEdit);
                actionCell.appendChild(btnDelete);
                actionCell.appendChild(btnSync);
                tr.appendChild(actionCell);
                tbody.appendChild(tr);
            });
        }

        function loadConnectors() {
            apiGet('/virtuallib/connectors').then(function (data) {
                renderConnectorTable(Array.isArray(data) ? data : []);
            }).catch(function (e) {
                console.error('VirtualLib: failed to load connectors', e);
            });
        }

        // -------------------------------------------------------------------
        // Add / Edit form
        // -------------------------------------------------------------------

        function openAddConnector() {
            q('connectorId').value = '';
            q('connectorName').value = '';
            q('connectorType').value = 'Emby';
            q('connectorUrl').value = '';
            q('connectorApiKey').value = '';
            q('librarySection').style.display = 'none';

            var libBox = q('libraryCheckboxes');
            while (libBox.firstChild) libBox.removeChild(libBox.firstChild);

            q('connectorFormTitle').textContent = 'Add Connector';
            clearStatus(q('testResultMsg'));
            clearStatus(q('connectorSaveStatus'));
            q('connectorFormSection').style.display = '';
            q('connectorFormSection').scrollIntoView({ behavior: 'smooth' });
        }

        function openEditConnector(id) {
            apiGet('/virtuallib/connectors').then(function (connectors) {
                var c = connectors.find(function (x) { return x.Id === id; });
                if (!c) { Dashboard.alert('Connector not found.'); return; }

                q('connectorId').value = c.Id;
                q('connectorName').value = c.DisplayName || '';
                q('connectorType').value = c.ServerType || 'Emby';
                q('connectorUrl').value = c.ServerUrl || '';
                q('connectorApiKey').value = c.ApiKey || '';
                q('connectorFormTitle').textContent = 'Edit Connector';
                q('librarySection').style.display = 'none';

                var libBox = q('libraryCheckboxes');
                while (libBox.firstChild) libBox.removeChild(libBox.firstChild);

                clearStatus(q('testResultMsg'));
                clearStatus(q('connectorSaveStatus'));
                q('connectorFormSection').style.display = '';
                q('connectorFormSection').scrollIntoView({ behavior: 'smooth' });

                fetchAndRenderLibraries(id, c.LibraryIds || []);
            }).catch(function (e) {
                Dashboard.alert('Failed to load connectors: ' + e.message);
            });
        }

        function fetchAndRenderLibraries(connectorId, selectedIds) {
            var librarySection = q('librarySection');
            var container = q('libraryCheckboxes');
            while (container.firstChild) container.removeChild(container.firstChild);

            var loadingMsg = document.createElement('em');
            loadingMsg.textContent = 'Loading libraries\u2026';
            container.appendChild(loadingMsg);
            librarySection.style.display = '';

            if (!connectorId) {
                while (container.firstChild) container.removeChild(container.firstChild);
                var hint = document.createElement('em');
                hint.textContent = 'Save connector first, then libraries will appear here.';
                container.appendChild(hint);
                return;
            }

            apiGet('/virtuallib/connectors/' + encodeURIComponent(connectorId) + '/libraries')
                .then(function (libs) { renderLibraryCheckboxes(libs || [], selectedIds); })
                .catch(function (e) {
                    while (container.firstChild) container.removeChild(container.firstChild);
                    var errMsg = document.createElement('span');
                    errMsg.style.color = 'var(--theme-error-color, #e53935)';
                    errMsg.textContent = 'Failed to load libraries: ' + e.message;
                    container.appendChild(errMsg);
                });
        }

        function renderLibraryCheckboxes(libs, selectedIds) {
            var container = q('libraryCheckboxes');
            while (container.firstChild) container.removeChild(container.firstChild);

            if (!libs.length) {
                var msg = document.createElement('em');
                msg.textContent = 'No libraries found on this server.';
                container.appendChild(msg);
                return;
            }

            libs.forEach(function (lib) {
                var label = document.createElement('label');
                label.style.cssText = 'display:block;margin:4px 0;cursor:pointer';

                var cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.className = 'lib-checkbox';
                cb.value = lib.Id;
                cb.style.marginRight = '6px';
                if (selectedIds && selectedIds.indexOf(lib.Id) !== -1) cb.checked = true;

                var nameSpan = document.createTextNode(lib.Name + ' ');
                var typeSpan = document.createElement('span');
                typeSpan.style.cssText = 'opacity:0.6;font-size:0.85em';
                typeSpan.textContent = '(' + lib.Type + ')';

                label.appendChild(cb);
                label.appendChild(nameSpan);
                label.appendChild(typeSpan);
                container.appendChild(label);
            });
        }

        function getSelectedLibraryIds() {
            return Array.from(view.querySelectorAll('#libraryCheckboxes .lib-checkbox:checked'))
                .map(function (cb) { return cb.value; });
        }

        // -------------------------------------------------------------------
        // Delete connector
        // -------------------------------------------------------------------

        function deleteConnector(id) {
            if (!confirm('Delete this connector? Existing .strm files will NOT be removed.')) return;
            apiDelete('/virtuallib/connectors/' + encodeURIComponent(id))
                .then(function () { loadConnectors(); })
                .catch(function (e) { Dashboard.alert('Failed to delete: ' + e.message); });
        }

        // -------------------------------------------------------------------
        // Synchronisation
        // -------------------------------------------------------------------

        function startSyncUI() {
            q('syncProgressContainer').style.display = '';
            q('syncProgressBar').style.width = '0%';
            q('syncProgressBar').style.background = 'var(--accent-color, #00a4dc)';
            q('syncProgressLabel').textContent = 'Synchronising\u2026';
            q('syncResultContainer').style.display = 'none';
            q('syncResultLog').textContent = '';
        }

        function finishSyncUI(results, msg) {
            q('syncProgressBar').style.width = '100%';
            q('syncProgressLabel').textContent = msg || 'Done.';
            displaySyncResults(results, q('syncResultLog'), q('syncResultContainer'));
        }

        function errorSyncUI(message) {
            q('syncProgressLabel').textContent = 'Error: ' + message;
            q('syncProgressBar').style.background = 'var(--theme-error-color, #e53935)';
        }

        function syncSingleConnector(id) {
            startSyncUI();
            q('syncProgressBar').style.width = '30%';
            apiPost('/virtuallib/connectors/' + encodeURIComponent(id) + '/sync')
                .then(function (result) { finishSyncUI([result], 'Done.'); })
                .catch(function (e) { errorSyncUI(e.message); });
        }

        function displaySyncResults(results, logEl, containerEl) {
            var lines = [];
            if (!results || results.length === 0) {
                lines.push('No connectors were synced.');
            } else {
                results.forEach(function (r, i) {
                    lines.push('Connector ' + (i + 1) + ':');
                    if (!r.Success) {
                        lines.push('  Status : FAILED');
                        lines.push('  Error  : ' + (r.ErrorMessage || 'unknown'));
                    } else {
                        lines.push('  Status   : OK');
                        lines.push('  Created  : ' + r.ItemsCreated);
                        lines.push('  Skipped  : ' + r.ItemsSkipped);
                        lines.push('  Failed   : ' + r.ItemsFailed);
                        lines.push('  Duration : ' + formatDuration(r.Duration));
                    }
                    lines.push('');
                });
            }
            logEl.textContent = lines.join('\n');
            containerEl.style.display = '';
        }

        function formatDuration(iso) {
            if (!iso) return '?';
            var m = String(iso).match(/(\d+):(\d+):(\d+)/);
            if (m) {
                var h = parseInt(m[1], 10), min = parseInt(m[2], 10), s = parseInt(m[3], 10);
                if (h > 0) return h + 'h ' + min + 'm ' + s + 's';
                if (min > 0) return min + 'm ' + s + 's';
                return s + 's';
            }
            return String(iso);
        }

        // -------------------------------------------------------------------
        // Global settings
        // -------------------------------------------------------------------

        function loadGlobalSettings() {
            apiGet('/virtuallib/settings').then(function (s) {
                q('virtualLibRoot').value = s.VirtualLibraryRootPath || '';
                q('syncInterval').value = s.SyncIntervalHours || 6;
                q('proxyTimeout').value = s.ProxyTimeoutSeconds || 30;
            }).catch(function (e) {
                console.error('VirtualLib: failed to load settings', e);
            });
        }

        // -------------------------------------------------------------------
        // Init — called by Emby when the view is shown
        // -------------------------------------------------------------------

        view.addEventListener('viewshow', function () {
            loadGlobalSettings();
            loadConnectors();

            q('btnSaveGlobal').addEventListener('click', function () {
                var statusEl = q('globalSaveStatus');
                var payload = {
                    VirtualLibraryRootPath: q('virtualLibRoot').value.trim(),
                    SyncIntervalHours: parseInt(q('syncInterval').value, 10) || 6,
                    ProxyTimeoutSeconds: parseInt(q('proxyTimeout').value, 10) || 30
                };
                apiPut('/virtuallib/settings', payload)
                    .then(function () { setStatus(statusEl, 'Settings saved.', false); })
                    .catch(function (e) { setStatus(statusEl, 'Error: ' + e.message, true); });
            });

            q('btnAddConnector').addEventListener('click', openAddConnector);

            q('btnCancelConnector').addEventListener('click', function () {
                q('connectorFormSection').style.display = 'none';
            });

            q('btnTestConnection').addEventListener('click', function () {
                var statusEl = q('testResultMsg');
                var connectorId = q('connectorId').value;
                setStatus(statusEl, 'Testing\u2026', false);

                if (connectorId) {
                    apiPost('/virtuallib/connectors/' + encodeURIComponent(connectorId) + '/test')
                        .then(function (res) {
                            if (res.Success) {
                                setStatus(statusEl, 'Connected \u2014 server v' + (res.ServerVersion || '?'), false);
                                fetchAndRenderLibraries(connectorId, getSelectedLibraryIds());
                            } else {
                                setStatus(statusEl, 'Failed: ' + (res.ErrorMessage || 'unknown error'), true);
                            }
                        })
                        .catch(function (e) { setStatus(statusEl, 'Error: ' + e.message, true); });
                } else {
                    statusEl.textContent = 'Save the connector first to enable connection test.';
                    statusEl.style.color = 'var(--theme-warning-color, orange)';
                }
            });

            q('btnSaveConnector').addEventListener('click', function () {
                var statusEl = q('connectorSaveStatus');
                clearStatus(statusEl);

                var id = q('connectorId').value;
                var payload = {
                    DisplayName: q('connectorName').value.trim(),
                    ServerType: q('connectorType').value,
                    ServerUrl: q('connectorUrl').value.trim(),
                    ApiKey: q('connectorApiKey').value.trim(),
                    LibraryIds: getSelectedLibraryIds(),
                    Enabled: true
                };

                if (!payload.DisplayName || !payload.ServerUrl || !payload.ApiKey) {
                    setStatus(statusEl, 'Name, URL and API Key are required.', true);
                    return;
                }

                var p;
                if (id) {
                    payload.Id = id;
                    p = apiPut('/virtuallib/connectors/' + encodeURIComponent(id), payload)
                            .then(function () { setStatus(statusEl, 'Connector updated.', false); });
                } else {
                    p = apiPost('/virtuallib/connectors', payload)
                            .then(function () { setStatus(statusEl, 'Connector added.', false); });
                }

                p.then(function () {
                    q('connectorFormSection').style.display = 'none';
                    loadConnectors();
                }).catch(function (e) { setStatus(statusEl, 'Error: ' + e.message, true); });
            });

            q('btnSyncAll').addEventListener('click', function () {
                startSyncUI();
                apiPost('/virtuallib/sync')
                    .then(function (results) { finishSyncUI(results || [], 'Synchronisation complete.'); })
                    .catch(function (e) { errorSyncUI(e.message); });
            });
        });
    };
});
