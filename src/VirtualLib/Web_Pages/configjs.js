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
                btnSync.addEventListener('click', (function (id, name) { return function () { syncSingleConnector(id, name); }; }(c.Id, c.DisplayName)));

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

                        var libEnabled = (c.LibraryIds || []).indexOf(lib.Id) !== -1;

                        // Create sync button first so the checkbox listener can reference it
                        var btnLibSync = document.createElement('button');
                        btnLibSync.setAttribute('is', 'emby-button');
                        btnLibSync.className = 'emby-button';
                        btnLibSync.textContent = 'Sync';
                        btnLibSync.style.cssText = 'margin-left:8px;padding:2px 8px;font-size:0.85em';
                        btnLibSync.disabled = !libEnabled;
                        btnLibSync.addEventListener('click', (function (connId, libId) {
                            return function () { syncLibrary(connId, libId); };
                        }(c.Id, lib.Id)));

                        var label = document.createElement('label');
                        label.style.cssText = 'display:inline-flex;align-items:center;gap:6px;cursor:pointer;min-width:200px';

                        var cb = document.createElement('input');
                        cb.type = 'checkbox';
                        cb.checked = libEnabled;
                        cb.addEventListener('change', (function (connId, libId, syncBtn) {
                            return function () {
                                syncBtn.disabled = !this.checked;
                                toggleLibrary(connId, libId, this.checked);
                            };
                        }(c.Id, lib.Id, btnLibSync)));

                        var nameSpan = document.createElement('span');
                        nameSpan.textContent = lib.Name;

                        var typeSpan = document.createElement('span');
                        typeSpan.style.cssText = 'opacity:0.5;font-size:0.8em';
                        typeSpan.textContent = '(' + lib.Type + ')';

                        label.appendChild(cb);
                        label.appendChild(nameSpan);
                        label.appendChild(typeSpan);

                        var remoteCount = (lib.RemoteItemCount !== undefined && lib.RemoteItemCount >= 0)
                            ? lib.RemoteItemCount : '?';
                        var countSpan = document.createElement('span');
                        countSpan.style.cssText = 'margin-left:12px;opacity:0.6;font-size:0.85em';
                        countSpan.textContent = '\u2026 / ' + remoteCount + ' distant';
                        countSpan.setAttribute('data-count-lib', lib.Id)
                        countSpan.setAttribute('data-remote-count', remoteCount);

                        tdLib.appendChild(label);
                        tdLib.appendChild(countSpan);
                        tdLib.appendChild(btnLibSync);
                        trLib.appendChild(tdLib);
                        tbody.appendChild(trLib);
                    });

                    // Load local entry counts, then refresh remote counts from server
                    loadLibraryStats(c.Id);
                    refreshRemoteCounts(c.Id);
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

        function updateAuthModeVisibility() {
            var mode = q('connectorAuthMode').value;
            q('apiKeySection').style.display = mode === 'ApiKey' ? '' : 'none';
            q('userCredSection').style.display = mode === 'UserCredentials' ? '' : 'none';
        }

        function updateServerTypeVisibility() {
            var type = q('connectorType').value;
            var isPlexTv = type === 'PlexTV';
            q('serverUrlSection').style.display = isPlexTv ? 'none' : '';
            q('plexTvSection').style.display    = isPlexTv ? '' : 'none';
        }

        function loadPlexServers() {
            var statusEl = q('plexServersStatus');
            statusEl.textContent = 'Loading\u2026';
            statusEl.style.color = '';

            var authMode = q('connectorAuthMode').value;
            var payload  = {
                AuthMode:     authMode,
                ApiKey:       authMode === 'ApiKey' ? q('connectorApiKey').value.trim() : '',
                Username:     authMode === 'UserCredentials' ? q('connectorUsername').value.trim() : '',
                Password:     authMode === 'UserCredentials' ? q('connectorPassword').value : '',
                TwoFactorPin: q('plexTwoFactorPin').value.trim()
            };

            apiPost('/virtuallib/plex/servers', payload)
                .then(function (result) {
                    var servers = result.Servers || [];
                    var sel = q('plexMachineId');
                    while (sel.firstChild) sel.removeChild(sel.firstChild);

                    if (servers.length === 0) {
                        var opt = document.createElement('option');
                        opt.value = '';
                        opt.textContent = '\u2014 No servers found \u2014';
                        sel.appendChild(opt);
                        statusEl.textContent = 'No servers found.';
                        statusEl.style.color = 'var(--theme-error-color, #e53935)';
                        return;
                    }

                    servers.forEach(function (s) {
                        var opt = document.createElement('option');
                        opt.value = s.MachineIdentifier;
                        var label = s.Name;
                        if (s.IsLocal) label += ' (local)';
                        else if (s.IsRelay) label += ' (relay)';
                        else label += ' (plex.direct)';
                        opt.textContent = label;
                        sel.appendChild(opt);
                    });

                    // If authenticated via user/pass + 2FA, the backend resolved a long-lived token.
                    // Store it as the ApiKey and switch to ApiKey mode so syncs never need re-2FA.
                    if (authMode === 'UserCredentials' && result.ResolvedToken) {
                        q('connectorAuthMode').value = 'ApiKey';
                        q('connectorApiKey').value   = result.ResolvedToken;
                        updateAuthModeVisibility();
                        statusEl.textContent = servers.length + ' server(s) found. Token saved as API Key — 2FA will not be required for syncs.';
                    } else {
                        statusEl.textContent = servers.length + ' server(s) found.';
                    }
                    statusEl.style.color = 'var(--theme-success-color, #43a047)';
                })
                .catch(function (e) {
                    statusEl.textContent = 'Error: ' + e.message;
                    statusEl.style.color = 'var(--theme-error-color, #e53935)';
                });
        }

        function resetPlexServerPicker() {
            var sel = q('plexMachineId');
            while (sel.firstChild) sel.removeChild(sel.firstChild);
            var opt = document.createElement('option');
            opt.value = '';
            opt.textContent = '\u2014 Enter credentials above and click Load Servers \u2014';
            sel.appendChild(opt);
        }

        function openAddConnector() {
            q('connectorId').value = '';
            q('connectorName').value = '';
            q('connectorType').value = 'Emby';
            q('connectorUrl').value = '';
            q('connectorAuthMode').value = 'ApiKey';
            q('connectorApiKey').value = '';
            q('connectorUsername').value = '';
            q('connectorPassword').value = '';
            q('connectorMetadataMode').value = 'RemoteSync';
            q('plexTwoFactorPin').value = '';
            resetPlexServerPicker();
            clearStatus(q('plexServersStatus'));
            updateAuthModeVisibility();
            updateServerTypeVisibility();
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
                q('connectorAuthMode').value = c.AuthMode || 'ApiKey';
                q('connectorApiKey').value = c.ApiKey || '';
                q('connectorUsername').value = c.Username || '';
                q('connectorPassword').value = '';  // never pre-fill password
                q('connectorMetadataMode').value = c.MetadataMode || 'RemoteSync';

                // Restore PlexTV machine picker with saved identifier
                var sel = q('plexMachineId');
                while (sel.firstChild) sel.removeChild(sel.firstChild);
                if (c.ServerType === 'PlexTV' && c.PlexMachineIdentifier) {
                    var savedOpt = document.createElement('option');
                    savedOpt.value = c.PlexMachineIdentifier;
                    savedOpt.textContent = c.PlexMachineIdentifier + ' \u2014 click Load Servers to refresh';
                    sel.appendChild(savedOpt);
                } else {
                    resetPlexServerPicker();
                }
                clearStatus(q('plexServersStatus'));

                updateAuthModeVisibility();
                updateServerTypeVisibility();
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
                .then(function () {
                    loadConnectors();
                    refreshRemoteCounts(connectorId);
                })
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

        function syncSingleConnector(id, displayName) {
            startSyncUI();
            var bar = q('syncProgressBar');
            var label = q('syncProgressLabel');
            bar.style.width = '10%';
            label.textContent = (displayName ? displayName + ' \u2014 ' : '') + 'Connecting\u2026';
            // Animate bar while waiting (10% → 80% over ~2s)
            var pct = 10;
            var timer = setInterval(function () {
                pct = Math.min(pct + 5, 80);
                bar.style.width = pct + '%';
            }, 400);
            apiPost('/virtuallib/connectors/' + encodeURIComponent(id) + '/sync')
                .then(function (result) {
                    clearInterval(timer);
                    finishSyncUI([result], 'Done.');
                    loadLibraryStats(id);
                })
                .catch(function (e) {
                    clearInterval(timer);
                    errorSyncUI(e.message);
                });
        }

        function refreshRemoteCounts(connectorId) {
            apiGet('/virtuallib/connectors/' + encodeURIComponent(connectorId) + '/item-counts')
                .then(function (stats) {
                    if (!stats || !stats.length) return;
                    stats.forEach(function (s) {
                        view.querySelectorAll('[data-count-lib="' + s.LibraryId + '"]').forEach(function (span) {
                            var local = span.getAttribute('data-local-count') || '\u2026';
                            span.textContent = local + ' / ' + s.RemoteItemCount + ' distant';
                            span.setAttribute('data-remote-count', s.RemoteItemCount);
                        });
                    });
                })
                .catch(function () { /* silently ignore */ });
        }

        function loadLibraryStats(connectorId) {
            apiGet('/virtuallib/connectors/' + encodeURIComponent(connectorId) + '/stats')
                .then(function (stats) {
                    if (!stats || !stats.length) return;
                    stats.forEach(function (s) {
                        view.querySelectorAll('[data-count-lib="' + s.LibraryId + '"]').forEach(function (span) {
                            var remoteCount = span.getAttribute('data-remote-count') || '?';
                            span.textContent = s.EntryCount + ' locaux / ' + remoteCount + ' distant';
                            span.setAttribute('data-local-count', s.EntryCount);
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
                    AuthMode: c.AuthMode || 'ApiKey',
                    ApiKey: c.ApiKey,
                    Username: c.Username || '',
                    Password: '',  // empty = preserve existing on server side
                    MetadataMode: c.MetadataMode || 'RemoteSync',
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
            q('connectorAuthMode').addEventListener('change', updateAuthModeVisibility);
            q('connectorType').addEventListener('change', updateServerTypeVisibility);
            q('btnLoadPlexServers').addEventListener('click', loadPlexServers);

            q('btnCancelConnector').addEventListener('click', function () {
                q('connectorFormSection').style.display = 'none';
            });

            q('btnTestConnection').addEventListener('click', function () {
                var statusEl = q('testResultMsg');
                var connectorId = q('connectorId').value;
                setStatus(statusEl, 'Testing\u2026', false);

                // Always test with current form values — works before and after save
                var connType = q('connectorType').value;
                var params = {
                    ServerType:           connType,
                    ServerUrl:            connType === 'PlexTV' ? '' : q('connectorUrl').value.trim(),
                    PlexMachineIdentifier: connType === 'PlexTV' ? q('plexMachineId').value : '',
                    AuthMode:             q('connectorAuthMode').value,
                    ApiKey:               q('connectorApiKey').value.trim(),
                    Username:             q('connectorUsername').value.trim(),
                    Password:             q('connectorPassword').value
                };

                apiPost('/virtuallib/test-connection', params)
                    .then(function (res) {
                        if (res.Success) {
                            setStatus(statusEl, 'Connected \u2014 server v' + (res.ServerVersion || '?'), false);
                            // If the connector is already saved, refresh its library list
                            if (connectorId) refreshKnownLibraries(connectorId);
                        } else {
                            setStatus(statusEl, 'Failed: ' + (res.ErrorMessage || 'unknown error'), true);
                        }
                    })
                    .catch(function (e) { setStatus(statusEl, 'Error: ' + e.message, true); });
            });

            q('btnSaveConnector').addEventListener('click', function () {
                var statusEl = q('connectorSaveStatus');
                clearStatus(statusEl);

                var id = q('connectorId').value;
                var displayName = q('connectorName').value.trim();
                var serverType  = q('connectorType').value;
                var serverUrl   = serverType === 'PlexTV' ? '' : q('connectorUrl').value.trim();
                var plexMachineId = serverType === 'PlexTV' ? q('plexMachineId').value : '';
                var authMode    = q('connectorAuthMode').value;
                var apiKey      = q('connectorApiKey').value.trim();
                var username    = q('connectorUsername').value.trim();
                var password    = q('connectorPassword').value;
                var metadataMode = q('connectorMetadataMode').value;

                if (!displayName) {
                    setStatus(statusEl, 'Name is required.', true);
                    return;
                }
                if (serverType !== 'PlexTV' && !serverUrl) {
                    setStatus(statusEl, 'Server URL is required.', true);
                    return;
                }
                if (serverType === 'PlexTV' && !plexMachineId) {
                    setStatus(statusEl, 'Select a Plex server (click Load Servers first).', true);
                    return;
                }
                if (authMode === 'ApiKey' && !apiKey) {
                    setStatus(statusEl, 'API Key is required in API Key mode.', true);
                    return;
                }
                if (authMode === 'UserCredentials' && !username) {
                    setStatus(statusEl, 'Username is required in User Credentials mode.', true);
                    return;
                }

                if (id) {
                    // Edit: preserve existing LibraryIds (managed via table checkboxes)
                    apiGet('/virtuallib/connectors').then(function (connectors) {
                        var existing = connectors.find(function (x) { return x.Id === id; });
                        var payload = {
                            Id: id,
                            DisplayName: displayName,
                            ServerType: serverType,
                            ServerUrl: serverUrl,
                            PlexMachineIdentifier: plexMachineId,
                            AuthMode: authMode,
                            ApiKey: apiKey,
                            Username: username,
                            Password: password,  // empty = preserve existing on server side
                            MetadataMode: metadataMode,
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
                    if (authMode === 'UserCredentials' && !password) {
                        setStatus(statusEl, 'Password is required for a new User Credentials connector.', true);
                        return;
                    }
                    var payload = {
                        DisplayName: displayName,
                        ServerType: serverType,
                        ServerUrl: serverUrl,
                        PlexMachineIdentifier: plexMachineId,
                        AuthMode: authMode,
                        ApiKey: apiKey,
                        Username: username,
                        Password: password,
                        MetadataMode: metadataMode,
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
                apiGet('/virtuallib/connectors').then(function (connectors) {
                    var enabled = (connectors || []).filter(function (c) { return c.Enabled; });
                    if (!enabled.length) {
                        finishSyncUI([], 'No enabled connectors.');
                        return;
                    }
                    var results = [];
                    var bar = q('syncProgressBar');
                    var label = q('syncProgressLabel');

                    function syncNext(i) {
                        if (i >= enabled.length) {
                            bar.style.width = '100%';
                            finishSyncUI(results, 'Synchronisation complete.');
                            enabled.forEach(function (c) { loadLibraryStats(c.Id); });
                            return;
                        }
                        var c = enabled[i];
                        var pct = Math.round((i / enabled.length) * 100);
                        bar.style.width = pct + '%';
                        label.textContent = '(' + (i + 1) + '/' + enabled.length + ') ' + c.DisplayName + '\u2026';
                        apiPost('/virtuallib/connectors/' + encodeURIComponent(c.Id) + '/sync')
                            .then(function (result) {
                                results.push(result);
                                syncNext(i + 1);
                            })
                            .catch(function (e) {
                                results.push({ ConnectorName: c.DisplayName, Success: false, ErrorMessage: e.message });
                                syncNext(i + 1);
                            });
                    }
                    syncNext(0);
                }).catch(function (e) { errorSyncUI(e.message); });
            });
        });
    };
});
