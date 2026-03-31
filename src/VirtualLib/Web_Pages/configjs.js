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
                // --- Connector header row ---
                var tr = document.createElement('tr');
                tr.style.cssText = 'border-top:1px solid rgba(255,255,255,0.1)';

                function td(text, style) {
                    var cell = document.createElement('td');
                    cell.style.cssText = 'padding:6px 8px' + (style ? ';' + style : '');
                    cell.textContent = text;
                    return cell;
                }

                tr.appendChild(td(c.DisplayName || ''));
                tr.appendChild(td(c.ServerType || ''));
                tr.appendChild(td(c.ServerUrl || ''));

                var actionCell = document.createElement('td');
                actionCell.style.cssText = 'padding:6px 8px';

                var btnEdit = document.createElement('button');
                btnEdit.setAttribute('is', 'emby-button');
                btnEdit.className = 'emby-button';
                btnEdit.textContent = 'Edit';
                btnEdit.style.marginRight = '4px';
                btnEdit.addEventListener('click', (function (id) { return function () { openEditConnector(id); }; }(c.Id)));

                var btnDelete = document.createElement('button');
                btnDelete.setAttribute('is', 'emby-button');
                btnDelete.className = 'emby-button';
                btnDelete.textContent = 'Delete';
                btnDelete.style.marginRight = '4px';
                btnDelete.addEventListener('click', (function (id) { return function () { deleteConnector(id); }; }(c.Id)));

                var btnSync = document.createElement('button');
                btnSync.setAttribute('is', 'emby-button');
                btnSync.className = 'emby-button';
                btnSync.textContent = 'Sync All';
                btnSync.addEventListener('click', (function (id) { return function () { syncSingleConnector(id); }; }(c.Id)));

                actionCell.appendChild(btnEdit);
                actionCell.appendChild(btnDelete);
                actionCell.appendChild(btnSync);
                tr.appendChild(actionCell);
                tbody.appendChild(tr);

                // --- Library sub-rows ---
                var libs = c.KnownLibraries || [];
                if (libs.length === 0) {
                    var trNoLib = document.createElement('tr');
                    var tdNoLib = document.createElement('td');
                    tdNoLib.colSpan = 4;
                    tdNoLib.style.cssText = 'padding:4px 8px 4px 2em;opacity:0.5;font-size:0.85em';
                    tdNoLib.textContent = 'No libraries discovered yet \u2014 click Test Connection in the Edit form.';
                    trNoLib.appendChild(tdNoLib);
                    tbody.appendChild(trNoLib);
                } else {
                    libs.forEach(function (lib) {
                        var trLib = document.createElement('tr');
                        var tdLib = document.createElement('td');
                        tdLib.colSpan = 4;
                        tdLib.style.cssText = 'padding:2px 8px 2px 2em';

                        var label = document.createElement('label');
                        label.style.cssText = 'display:inline-flex;align-items:center;gap:6px;cursor:pointer;min-width:200px';

                        var cb = document.createElement('input');
                        cb.type = 'checkbox';
                        cb.checked = (c.LibraryIds || []).indexOf(lib.Id) !== -1;
                        cb.addEventListener('change', (function (connId, libId) {
                            return function () { toggleLibrary(connId, libId, this.checked); };
                        }(c.Id, lib.Id)));

                        var nameSpan = document.createElement('span');
                        nameSpan.textContent = lib.Name;

                        var typeSpan = document.createElement('span');
                        typeSpan.style.cssText = 'opacity:0.5;font-size:0.8em';
                        typeSpan.textContent = '(' + lib.Type + ')';

                        label.appendChild(cb);
                        label.appendChild(nameSpan);
                        label.appendChild(typeSpan);

                        var countSpan = document.createElement('span');
                        countSpan.style.cssText = 'margin-left:12px;opacity:0.6;font-size:0.85em';
                        countSpan.textContent = '\u2026';
                        countSpan.setAttribute('data-count-lib', lib.Id);

                        var btnLibSync = document.createElement('button');
                        btnLibSync.setAttribute('is', 'emby-button');
                        btnLibSync.className = 'emby-button';
                        btnLibSync.textContent = 'Sync';
                        btnLibSync.style.cssText = 'margin-left:8px;padding:2px 8px;font-size:0.85em';
                        btnLibSync.addEventListener('click', (function (connId, libId) {
                            return function () { syncLibrary(connId, libId); };
                        }(c.Id, lib.Id)));

                        tdLib.appendChild(label);
                        tdLib.appendChild(countSpan);
                        tdLib.appendChild(btnLibSync);
                        trLib.appendChild(tdLib);
                        tbody.appendChild(trLib);
                    });

                    // Load entry counts asynchronously
                    loadLibraryStats(c.Id);
                }
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
                clearStatus(q('testResultMsg'));
                clearStatus(q('connectorSaveStatus'));
                q('connectorFormSection').style.display = '';
                q('connectorFormSection').scrollIntoView({ behavior: 'smooth' });
            }).catch(function (e) {
                Dashboard.alert('Failed to load connectors: ' + e.message);
            });
        }

        function refreshKnownLibraries(connectorId) {
            // Silently refresh KnownLibraries cache on the server, then reload the table
            apiGet('/virtuallib/connectors/' + encodeURIComponent(connectorId) + '/libraries')
                .then(function () { loadConnectors(); })
                .catch(function () { /* ignore */ });
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
                .then(function (result) {
                    finishSyncUI([result], 'Done.');
                    loadLibraryStats(id);
                })
                .catch(function (e) { errorSyncUI(e.message); });
        }

        function loadLibraryStats(connectorId) {
            apiGet('/virtuallib/connectors/' + encodeURIComponent(connectorId) + '/stats')
                .then(function (stats) {
                    if (!stats || !stats.length) return;
                    stats.forEach(function (s) {
                        view.querySelectorAll('[data-count-lib="' + s.LibraryId + '"]').forEach(function (span) {
                            span.textContent = s.EntryCount + ' entries';
                        });
                    });
                })
                .catch(function () { /* silently ignore */ });
        }

        function toggleLibrary(connectorId, libraryId, enabled) {
            apiGet('/virtuallib/connectors').then(function (connectors) {
                var c = connectors.find(function (x) { return x.Id === connectorId; });
                if (!c) return;
                var ids = (c.LibraryIds || []).slice();
                if (enabled && ids.indexOf(libraryId) === -1) ids.push(libraryId);
                if (!enabled) ids = ids.filter(function (id) { return id !== libraryId; });
                var payload = {
                    Id: c.Id,
                    DisplayName: c.DisplayName,
                    ServerType: c.ServerType,
                    ServerUrl: c.ServerUrl,
                    ApiKey: c.ApiKey,
                    LibraryIds: ids,
                    Enabled: c.Enabled
                };
                return apiPut('/virtuallib/connectors/' + encodeURIComponent(connectorId), payload);
            }).catch(function (e) {
                console.error('VirtualLib: failed to toggle library', e);
            });
        }

        function syncLibrary(connectorId, libraryId) {
            startSyncUI();
            apiPost('/virtuallib/connectors/' + encodeURIComponent(connectorId) + '/libraries/' + encodeURIComponent(libraryId) + '/sync')
                .then(function (result) {
                    finishSyncUI([result], 'Done.');
                    loadLibraryStats(connectorId);
                })
                .catch(function (e) { errorSyncUI(e.message); });
        }

        function displaySyncResults(results, logEl, containerEl) {
            var lines = [];
            if (!results || results.length === 0) {
                lines.push('No connectors were synced.');
            } else {
                results.forEach(function (r) {
                    var name = r.ConnectorName || 'Connector';
                    lines.push('\u25B6 ' + name);
                    if (!r.Success) {
                        lines.push('  Status : FAILED');
                        lines.push('  Error  : ' + (r.ErrorMessage || 'unknown'));
                    } else {
                        lines.push('  Status   : OK');
                        lines.push('  Duration : ' + formatDuration(r.Duration));
                        if (r.Libraries && r.Libraries.length > 0) {
                            r.Libraries.forEach(function (lib) {
                                lines.push('  \u2022 ' + lib.LibraryName);
                                lines.push('      Created  : ' + lib.ItemsCreated);
                                lines.push('      Skipped  : ' + lib.ItemsSkipped);
                                lines.push('      Failed   : ' + lib.ItemsFailed);
                            });
                        } else {
                            lines.push('  Created  : ' + r.ItemsCreated);
                            lines.push('  Skipped  : ' + r.ItemsSkipped);
                            lines.push('  Failed   : ' + r.ItemsFailed);
                        }
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
                q('proxyBaseUrl').value = s.ProxyBaseUrl || '';
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
                    ProxyBaseUrl: q('proxyBaseUrl').value.trim(),
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
                                refreshKnownLibraries(connectorId);
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
                var displayName = q('connectorName').value.trim();
                var serverUrl = q('connectorUrl').value.trim();
                var apiKey = q('connectorApiKey').value.trim();

                if (!displayName || !serverUrl || !apiKey) {
                    setStatus(statusEl, 'Name, URL and API Key are required.', true);
                    return;
                }

                var p;
                if (id) {
                    // Edit: preserve existing LibraryIds (managed via table checkboxes)
                    apiGet('/virtuallib/connectors').then(function (connectors) {
                        var existing = connectors.find(function (x) { return x.Id === id; });
                        var payload = {
                            Id: id,
                            DisplayName: displayName,
                            ServerType: q('connectorType').value,
                            ServerUrl: serverUrl,
                            ApiKey: apiKey,
                            LibraryIds: existing ? (existing.LibraryIds || []) : [],
                            Enabled: true
                        };
                        return apiPut('/virtuallib/connectors/' + encodeURIComponent(id), payload);
                    }).then(function () {
                        setStatus(statusEl, 'Connector updated.', false);
                        q('connectorFormSection').style.display = 'none';
                        loadConnectors();
                    }).catch(function (e) { setStatus(statusEl, 'Error: ' + e.message, true); });
                } else {
                    var payload = {
                        DisplayName: displayName,
                        ServerType: q('connectorType').value,
                        ServerUrl: serverUrl,
                        ApiKey: apiKey,
                        LibraryIds: [],
                        Enabled: true
                    };
                    apiPost('/virtuallib/connectors', payload)
                        .then(function () {
                            setStatus(statusEl, 'Connector added.', false);
                            q('connectorFormSection').style.display = 'none';
                            loadConnectors();
                        }).catch(function (e) { setStatus(statusEl, 'Error: ' + e.message, true); });
                }
            });

            q('btnSyncAll').addEventListener('click', function () {
                startSyncUI();
                apiPost('/virtuallib/sync')
                    .then(function (results) {
                        finishSyncUI(results || [], 'Synchronisation complete.');
                        apiGet('/virtuallib/connectors').then(function (connectors) {
                            connectors.forEach(function (c) { loadLibraryStats(c.Id); });
                        }).catch(function () {});
                    })
                    .catch(function (e) { errorSyncUI(e.message); });
            });
        });
    };
});
