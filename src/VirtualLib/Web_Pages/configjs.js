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

        var TYPE_LABELS = { Movies: 'Movies', TvShows: 'TV Shows', Music: 'Music' };

        // Local cache of connectors — updated on every loadConnectors() call.
        // Used by toggleLibrary() to avoid a GET→PUT race condition.
        var _connectorsCache = [];

        function makeBtn(text, onClick) {
            var btn = document.createElement('button');
            btn.setAttribute('is', 'emby-button');
            btn.className = 'emby-button';
            btn.textContent = text;
            btn.addEventListener('click', onClick);
            return btn;
        }

        function renderConnectorTree(connectors) {
            var tree = q('connectorTree');
            var noMsg = q('noConnectorsMsg');

            while (tree.firstChild) tree.removeChild(tree.firstChild);

            if (!connectors || connectors.length === 0) {
                noMsg.style.display = '';
                return;
            }
            noMsg.style.display = 'none';

            connectors.slice().sort(function (a, b) {
                return (a.DisplayName || '').localeCompare(b.DisplayName || '');
            }).forEach(function (c) {
                var libs = (c.KnownLibraries || []).slice().sort(function (a, b) {
                    return (a.Name || '').localeCompare(b.Name || '');
                });

                // Group libraries by type, sorted alphabetically
                var byType = {};
                libs.forEach(function (lib) {
                    var t = lib.Type || 'Unknown';
                    if (!byType[t]) byType[t] = [];
                    byType[t].push(lib);
                });
                var typeOrder = Object.keys(byType).sort(function (a, b) {
                    return (TYPE_LABELS[a] || a).localeCompare(TYPE_LABELS[b] || b);
                });

                // --- Connector node ---
                var connDiv = document.createElement('div');
                connDiv.className = 'vl-connector vl-collapsed';

                // Header
                var header = document.createElement('div');
                header.className = 'vl-connector-header';

                var caret = document.createElement('span');
                caret.className = 'vl-caret';
                caret.textContent = '\u25BC'; // ▼

                var nameSpan = document.createElement('span');
                nameSpan.className = 'vl-connector-name';
                nameSpan.textContent = c.DisplayName || '';

                var badge = document.createElement('span');
                badge.className = 'vl-badge';
                badge.textContent = c.ServerType || '';

                var summary = document.createElement('span');
                summary.setAttribute('data-connector-summary', c.Id);
                summary.style.cssText = 'font-size:0.82em;opacity:0.55;margin-left:6px';

                var actions = document.createElement('div');
                actions.className = 'vl-connector-actions';
                actions.appendChild(makeBtn('Edit',   function () { openEditConnector(c.Id); }));
                actions.appendChild(makeBtn('Delete', function () { deleteConnector(c.Id); }));
                var connSyncBtn = makeBtn('Sync', function () { syncSingleConnector(c.Id); });
                connSyncBtn.setAttribute('data-sync-btn', '1');
                actions.appendChild(connSyncBtn);

                header.appendChild(caret);
                header.appendChild(nameSpan);
                header.appendChild(badge);
                header.appendChild(summary);
                header.appendChild(actions);

                header.addEventListener('click', function (e) {
                    if (e.target.closest('button')) return;
                    connDiv.classList.toggle('vl-collapsed');
                });

                connDiv.appendChild(header);

                // Body
                var body = document.createElement('div');
                body.className = 'vl-connector-body';

                if (libs.length === 0) {
                    var noLib = document.createElement('div');
                    noLib.style.cssText = 'padding:6px 8px;opacity:0.5;font-size:0.85em';
                    noLib.textContent = 'No libraries discovered yet \u2014 click Test Connection in the Edit form.';
                    body.appendChild(noLib);
                } else {
                    typeOrder.forEach(function (type) {
                        var typeLibs = byType[type];
                        var typeLabel = TYPE_LABELS[type] || type;

                        // Type group
                        var typeGroup = document.createElement('div');
                        typeGroup.className = 'vl-type-group vl-collapsed';

                        var typeHeader = document.createElement('div');
                        typeHeader.className = 'vl-type-header';

                        var typeCaret = document.createElement('span');
                        typeCaret.className = 'vl-caret';
                        typeCaret.textContent = '\u25BC';

                        var typeTitle = document.createElement('span');
                        typeTitle.textContent = typeLabel;

                        var typeSummary = document.createElement('span');
                        typeSummary.setAttribute('data-type-summary', c.Id + '|' + type);
                        typeSummary.style.cssText = 'font-size:0.82em;opacity:0.55;margin-left:6px';

                        typeHeader.appendChild(typeCaret);
                        typeHeader.appendChild(typeTitle);
                        typeHeader.appendChild(typeSummary);
                        typeHeader.addEventListener('click', function () {
                            typeGroup.classList.toggle('vl-collapsed');
                        });

                        typeGroup.appendChild(typeHeader);

                        var typeBody = document.createElement('div');
                        typeBody.className = 'vl-type-body';

                        typeLibs.forEach(function (lib) {
                            var libEnabled = (c.LibraryIds || []).indexOf(lib.Id) !== -1;

                            var libRow = document.createElement('div');
                            libRow.className = 'vl-library';

                            var cb = document.createElement('input');
                            cb.type = 'checkbox';
                            cb.checked = libEnabled;

                            var label = document.createElement('label');
                            label.style.cssText = 'display:inline-flex;align-items:center;cursor:pointer;flex-shrink:0';
                            label.appendChild(cb);

                            var libName = document.createElement('span');
                            libName.textContent = lib.Name;
                            libName.style.cssText = 'flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap';

                            var remoteCount = (lib.RemoteItemCount !== undefined && lib.RemoteItemCount >= 0)
                                ? lib.RemoteItemCount : '?';
                            var countSpan = document.createElement('span');
                            countSpan.className = 'vl-lib-count';
                            countSpan.textContent = '\u2026\u00A0/\u00A0' + remoteCount + ' distant';
                            countSpan.setAttribute('data-count-lib', lib.Id);
                            countSpan.setAttribute('data-connector-id', c.Id);
                            countSpan.setAttribute('data-type-key', c.Id + '|' + type);
                            countSpan.setAttribute('data-lib-selected', libEnabled ? '1' : '0');
                            countSpan.setAttribute('data-remote-count', remoteCount);

                            var syncBtn = makeBtn('Sync', (function (connId, libId) {
                                return function () { syncLibrary(connId, libId); };
                            }(c.Id, lib.Id)));
                            syncBtn.style.cssText = 'padding:2px 8px;font-size:0.82em';
                            syncBtn.setAttribute('data-sync-btn', '1');
                            syncBtn.setAttribute('data-lib-enabled', libEnabled ? 'true' : 'false');
                            syncBtn.disabled = !libEnabled;

                            cb.addEventListener('change', (function (connId, libId, libType, btn, cntSpan) {
                                return function () {
                                    btn.setAttribute('data-lib-enabled', this.checked ? 'true' : 'false');
                                    btn.disabled = !this.checked;
                                    cntSpan.setAttribute('data-lib-selected', this.checked ? '1' : '0');
                                    updateTypeSummary(connId, libType);
                                    updateConnectorSummary(connId);
                                    toggleLibrary(connId, libId, this.checked);
                                };
                            }(c.Id, lib.Id, type, syncBtn, countSpan)));

                            libRow.appendChild(label);
                            libRow.appendChild(libName);
                            libRow.appendChild(countSpan);
                            libRow.appendChild(syncBtn);
                            typeBody.appendChild(libRow);
                        });

                        typeGroup.appendChild(typeBody);
                        body.appendChild(typeGroup);
                        updateTypeSummary(c.Id, type);
                    });

                    loadLibraryStats(c.Id);
                    refreshRemoteCounts(c.Id);
                }

                connDiv.appendChild(body);
                tree.appendChild(connDiv);
                updateConnectorSummary(c.Id);
            });
        }

        function loadConnectors() {
            apiGet('/virtuallib/connectors').then(function (data) {
                _connectorsCache = Array.isArray(data) ? data : [];
                renderConnectorTree(_connectorsCache);
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
            q('connectorMaxParallel').value = 4;
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

                console.log('[VirtualLib] openEditConnector — connector from server:', JSON.stringify({
                    Id: c.Id,
                    ServerType: c.ServerType,
                    PlexMachineIdentifier: c.PlexMachineIdentifier,
                    AuthMode: c.AuthMode,
                    ApiKey: c.ApiKey ? '(set)' : '(empty)'
                }));

                q('connectorId').value = c.Id;
                q('connectorName').value = c.DisplayName || '';
                q('connectorType').value = c.ServerType || 'Emby';
                q('connectorUrl').value = c.ServerUrl || '';
                q('connectorAuthMode').value = c.AuthMode || 'ApiKey';
                q('connectorApiKey').value = c.ApiKey || '';
                q('connectorUsername').value = c.Username || '';
                q('connectorPassword').value = c.Password || '';
                q('connectorMetadataMode').value = c.MetadataMode || 'RemoteSync';
                q('connectorMaxParallel').value = c.MaxParallelLibraries || 4;

                // Restore PlexTV machine picker with saved identifier
                var sel = q('plexMachineId');
                while (sel.firstChild) sel.removeChild(sel.firstChild);
                if (c.ServerType === 'PlexTV' && c.PlexMachineIdentifier) {
                    var savedOpt = document.createElement('option');
                    savedOpt.value = c.PlexMachineIdentifier;
                    savedOpt.textContent = c.PlexMachineIdentifier + ' \u2014 click Load Servers to refresh';
                    sel.appendChild(savedOpt);
                    console.log('[VirtualLib] PlexMachineIdentifier restored in picker:', c.PlexMachineIdentifier);
                } else {
                    resetPlexServerPicker();
                    console.log('[VirtualLib] PlexMachineIdentifier is empty — picker reset to placeholder');
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
        // Synchronisation — state + polling
        // -------------------------------------------------------------------

        var _syncPollTimer = null;

        // ── Sync status constants (mirror LibrarySyncStatus enum) ──────────
        var SP_PENDING = 0, SP_P1 = 1, SP_P2 = 2, SP_DONE = 3, SP_FAILED = 4;
        var SP_BADGE_CLASS  = ['vl-sp-pending', 'vl-sp-p1', 'vl-sp-p2', 'vl-sp-done', 'vl-sp-failed'];
        var SP_BADGE_LABEL  = ['Pending', 'Files\u2026', 'Metadata\u2026', 'Done', 'Failed'];
        var SP_FILL_PHASE1  = 'var(--accent-color,#00a4dc)';
        var SP_FILL_PHASE2  = '#4caf50';
        var SP_FILL_FAILED  = 'var(--theme-error-color,#e53935)';

        function setSyncMode(active) {
            q('btnSyncAll').disabled = active;
            view.querySelectorAll('[data-sync-btn]').forEach(function (btn) {
                if (active) {
                    btn.disabled = true;
                } else {
                    var libEnabled = btn.getAttribute('data-lib-enabled');
                    btn.disabled = libEnabled === 'false';
                }
            });
        }

        function stopSyncPoll() {
            if (_syncPollTimer) { clearTimeout(_syncPollTimer); _syncPollTimer = null; }
        }

        function spPct(done, total) {
            return total > 0 ? Math.min(100, Math.round(done / total * 100)) : 0;
        }

        // Build a double-bar element: two side-by-side track+fill combos
        function makeDoubleBars(p1Done, p1Total, p2Done, p2Total, status) {
            var wrap = document.createElement('div');
            wrap.className = 'vl-sp-bars';

            function makeBar(done, total, color) {
                var bw = document.createElement('div');
                bw.className = 'vl-sp-bar-wrap';
                var track = document.createElement('div');
                track.className = 'vl-sp-track';
                var fill = document.createElement('div');
                fill.className = 'vl-sp-fill';
                fill.style.width = spPct(done, total) + '%';
                fill.style.background = color;
                track.appendChild(fill);
                var pctEl = document.createElement('div');
                pctEl.className = 'vl-sp-pct';
                pctEl.textContent = spPct(done, total) + '%';
                bw.appendChild(track);
                bw.appendChild(pctEl);
                return bw;
            }

            var p1Color = status === SP_FAILED ? SP_FILL_FAILED : SP_FILL_PHASE1;
            var p2Color = SP_FILL_PHASE2;
            wrap.appendChild(makeBar(p1Done, p1Total, p1Color));
            wrap.appendChild(makeBar(p2Done, p2Total, p2Color));
            return wrap;
        }

        function makeBadge(status) {
            var b = document.createElement('span');
            b.className = 'vl-sp-badge ' + (SP_BADGE_CLASS[status] || 'vl-sp-pending');
            b.textContent = SP_BADGE_LABEL[status] || '';
            return b;
        }

        // Compute cumulative Phase1/Phase2 progress across a list of LibrarySyncEntry
        function cumulative(libs) {
            var p1d = 0, p1t = 0, p2d = 0, p2t = 0;
            libs.forEach(function (lib) {
                p1d += lib.Phase1Done;
                p1t += lib.Phase1Total;
                p2d += lib.Phase2Done;
                p2t += lib.Phase2Total;
            });
            return { p1d: p1d, p1t: p1t, p2d: p2d, p2t: p2t };
        }

        // Derive an aggregate status from a list of libraries
        function aggregateStatus(libs) {
            if (libs.every(function (l) { return l.Status === SP_DONE; })) return SP_DONE;
            if (libs.some(function (l) { return l.Status === SP_FAILED; })) return SP_FAILED;
            if (libs.some(function (l) { return l.Status === SP_P2; })) return SP_P2;
            if (libs.some(function (l) { return l.Status === SP_P1; })) return SP_P1;
            return SP_PENDING;
        }

        // Full tree re-render from the Libraries array
        function renderSyncTree(libraries) {
            var container = q('syncTreeProgress');
            while (container.firstChild) container.removeChild(container.firstChild);

            if (!libraries || libraries.length === 0) return;

            // Group: connectorId → type → [libs]
            var byConn = {};
            libraries.forEach(function (lib) {
                if (!byConn[lib.ConnectorId])
                    byConn[lib.ConnectorId] = { name: lib.ConnectorName, types: {} };
                var types = byConn[lib.ConnectorId].types;
                var t = lib.MediaType || 'Unknown';
                if (!types[t]) types[t] = [];
                types[t].push(lib);
            });

            Object.keys(byConn).sort(function (a, b) {
                return byConn[a].name.localeCompare(byConn[b].name);
            }).forEach(function (connId) {
                var conn = byConn[connId];
                var allConnLibs = Object.values(conn.types).reduce(function (acc, arr) { return acc.concat(arr); }, []);
                var cAgg = cumulative(allConnLibs);
                var cStatus = aggregateStatus(allConnLibs);

                var section = document.createElement('div');
                section.className = 'vl-sp-section';

                // ── Connector row ────────────────────────────────────────────
                var connRow = document.createElement('div');
                connRow.className = 'vl-sp-conn-row';
                var connName = document.createElement('span');
                connName.className = 'vl-sp-name';
                connName.textContent = conn.name;
                connRow.appendChild(connName);
                connRow.appendChild(makeBadge(cStatus));
                connRow.appendChild(makeDoubleBars(cAgg.p1d, cAgg.p1t, cAgg.p2d, cAgg.p2t, cStatus));
                section.appendChild(connRow);

                // ── Media type rows ──────────────────────────────────────────
                Object.keys(conn.types).sort().forEach(function (type) {
                    var typeLibs = conn.types[type];
                    var tAgg = cumulative(typeLibs);
                    var tStatus = aggregateStatus(typeLibs);

                    var typeRow = document.createElement('div');
                    typeRow.className = 'vl-sp-type-row';
                    var typeName = document.createElement('span');
                    typeName.className = 'vl-sp-name';
                    typeName.textContent = (TYPE_LABELS[type] || type) + ' (' + typeLibs.length + ')';
                    typeRow.appendChild(typeName);
                    typeRow.appendChild(makeBadge(tStatus));
                    typeRow.appendChild(makeDoubleBars(tAgg.p1d, tAgg.p1t, tAgg.p2d, tAgg.p2t, tStatus));
                    section.appendChild(typeRow);

                    // ── Library rows ─────────────────────────────────────────
                    typeLibs.forEach(function (lib) {
                        var libRow = document.createElement('div');
                        libRow.className = 'vl-sp-lib-row';
                        var libName = document.createElement('span');
                        libName.className = 'vl-sp-name';
                        libName.textContent = lib.LibraryName;
                        libRow.appendChild(libName);
                        libRow.appendChild(makeBadge(lib.Status));
                        libRow.appendChild(makeDoubleBars(
                            lib.Phase1Done, lib.Phase1Total,
                            lib.Phase2Done, lib.Phase2Total,
                            lib.Status));
                        if (lib.Status === SP_FAILED && lib.ErrorMessage) {
                            var errSpan = document.createElement('span');
                            errSpan.title = lib.ErrorMessage;
                            errSpan.textContent = '\u26a0';
                            errSpan.style.cssText = 'cursor:help;color:var(--theme-error-color,#e53935);flex-shrink:0';
                            libRow.appendChild(errSpan);
                        }
                        section.appendChild(libRow);
                    });
                });

                container.appendChild(section);
            });
        }

        function resetSyncTree() {
            var container = q('syncTreeProgress');
            while (container.firstChild) container.removeChild(container.firstChild);
            var lbl = q('syncStatusLabel');
            lbl.style.color = SP_FILL_PHASE1;
            lbl.textContent = '\u25cf Starting\u2026';
        }

        function finishSyncTree(libraries) {
            var lbl = q('syncStatusLabel');
            lbl.style.color = SP_FILL_PHASE2;
            lbl.textContent = '\u2713 Synchronisation complete';
            if (libraries && libraries.length > 0) renderSyncTree(libraries);
        }

        // Called repeatedly while a sync is in progress.
        // Also called once on page load to detect an already-running sync.
        function pollSyncStatus() {
            apiGet('/virtuallib/sync/status').then(function (status) {
                if (status.IsSyncing) {
                    q('syncProgressContainer').style.display = '';
                    q('syncResultContainer').style.display = 'none';
                    var lbl = q('syncStatusLabel');
                    lbl.style.color = SP_FILL_PHASE1;
                    lbl.textContent = '\u25cf Synchronising\u2026';
                    renderSyncTree(status.Libraries);
                    setSyncMode(true);
                    _syncPollTimer = setTimeout(pollSyncStatus, 2000);
                } else {
                    stopSyncPoll();
                    setSyncMode(false);
                    if (q('syncProgressContainer').style.display !== 'none') {
                        finishSyncTree(status.Libraries);
                        if (status.LastResults && status.LastResults.length > 0)
                            displaySyncResults(status.LastResults, q('syncResultLog'), q('syncResultContainer'));
                        loadConnectors();
                    }
                }
            }).catch(function () {
                stopSyncPoll();
                setSyncMode(false);
            });
        }

        // Start a sync from the UI: POST endpoint (returns immediately), then poll
        function startSync(url) {
            q('syncProgressContainer').style.display = '';
            resetSyncTree();
            q('syncResultContainer').style.display = 'none';
            q('syncResultLog').textContent = '';
            setSyncMode(true);

            apiPost(url).then(function (res) {
                if (res && res.AlreadyRunning) {
                    var lbl = q('syncStatusLabel');
                    lbl.textContent = 'A sync is already in progress.';
                }
                _syncPollTimer = setTimeout(pollSyncStatus, 1000);
            }).catch(function (e) {
                var lbl = q('syncStatusLabel');
                lbl.style.color = 'var(--theme-error-color,#e53935)';
                lbl.textContent = 'Error: ' + e.message;
                setSyncMode(false);
            });
        }

        function syncSingleConnector(id) {
            startSync('/virtuallib/connectors/' + encodeURIComponent(id) + '/sync');
        }

        function computeSummary(spans) {
            var totalLibs = spans.length;
            var selectedLibs = 0, selectedItems = 0, totalItems = 0, hasUnknown = false;
            spans.forEach(function (s) {
                var selected = s.getAttribute('data-lib-selected') === '1';
                var count = parseInt(s.getAttribute('data-remote-count'), 10);
                if (selected) selectedLibs++;
                if (isNaN(count) || count < 0) hasUnknown = true;
                else { totalItems += count; if (selected) selectedItems += count; }
            });
            var libPart = selectedLibs + '\u00A0/\u00A0' + totalLibs + '\u00A0lib' + (totalLibs !== 1 ? 's' : '');
            var itemPart = (hasUnknown && totalItems === 0)
                ? '\u2026'
                : selectedItems.toLocaleString() + '\u00A0/\u00A0' + totalItems.toLocaleString() + '\u00A0items';
            return libPart + '\u2003\u00B7\u2003' + itemPart;
        }

        function updateConnectorSummary(connectorId) {
            var el = view.querySelector('[data-connector-summary="' + connectorId + '"]');
            if (!el) return;
            el.textContent = computeSummary(
                Array.from(view.querySelectorAll('[data-connector-id="' + connectorId + '"]'))
            );
        }

        function updateTypeSummary(connectorId, type) {
            var key = connectorId + '|' + type;
            var el = view.querySelector('[data-type-summary="' + key + '"]');
            if (!el) return;
            el.textContent = computeSummary(
                Array.from(view.querySelectorAll('[data-type-key="' + key + '"]'))
            );
        }

        function refreshRemoteCounts(connectorId) {
            apiGet('/virtuallib/connectors/' + encodeURIComponent(connectorId) + '/item-counts')
                .then(function (stats) {
                    if (!stats || !stats.length) return;
                    var typeKeys = new Set();
                    stats.forEach(function (s) {
                        view.querySelectorAll('[data-count-lib="' + s.LibraryId + '"]').forEach(function (span) {
                            var local = span.getAttribute('data-local-count') || '\u2026';
                            span.textContent = local + ' / ' + s.RemoteItemCount + ' distant';
                            span.setAttribute('data-remote-count', s.RemoteItemCount);
                            var tk = span.getAttribute('data-type-key');
                            if (tk) typeKeys.add(tk);
                        });
                    });
                    typeKeys.forEach(function (key) {
                        var parts = key.split('|');
                        updateTypeSummary(parts[0], parts[1]);
                    });
                    updateConnectorSummary(connectorId);
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
            var c = _connectorsCache.find(function (x) { return x.Id === connectorId; });
            if (!c) return;
            var ids = (c.LibraryIds || []).slice();
            if (enabled && ids.indexOf(libraryId) === -1) ids.push(libraryId);
            if (!enabled) ids = ids.filter(function (id) { return id !== libraryId; });
            // Update cache immediately so rapid successive toggles don't race
            c.LibraryIds = ids;
            var payload = {
                Id: c.Id,
                DisplayName: c.DisplayName,
                ServerType: c.ServerType,
                ServerUrl: c.ServerUrl,
                PlexMachineIdentifier: c.PlexMachineIdentifier || '',
                AuthMode: c.AuthMode || 'ApiKey',
                ApiKey: c.ApiKey,
                Username: c.Username || '',
                Password: '',  // empty = preserve existing on server side
                MetadataMode: c.MetadataMode || 'RemoteSync',
                LibraryIds: ids,
                Enabled: c.Enabled
            };
            apiPut('/virtuallib/connectors/' + encodeURIComponent(connectorId), payload)
                .catch(function (e) {
                    console.error('VirtualLib: failed to toggle library', e);
                });
        }

        function syncLibrary(connectorId, libraryId) {
            startSync('/virtuallib/connectors/' + encodeURIComponent(connectorId) + '/libraries/' + encodeURIComponent(libraryId) + '/sync');
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

        view.addEventListener('viewhide', function () {
            stopSyncPoll();
        });

        view.addEventListener('viewshow', function () {
            loadGlobalSettings();
            loadConnectors();
            pollSyncStatus(); // Check if a sync was already in progress (e.g. after page refresh)

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
                var statusEl    = q('testResultMsg');
                var connectorId = q('connectorId').value;
                var connType    = q('connectorType').value;
                var machineId   = connType === 'PlexTV' ? q('plexMachineId').value : '';
                setStatus(statusEl, 'Testing\u2026', false);

                console.log('[VirtualLib] Test Connection clicked —', {
                    connType: connType,
                    connectorId: connectorId || '(new)',
                    machineId: machineId || '(empty)',
                    authMode: q('connectorAuthMode').value
                });

                function onSuccess(res) {
                    console.log('[VirtualLib] Test result:', res);
                    if (res.Success) {
                        setStatus(statusEl, 'Connected \u2014 server v' + (res.ServerVersion || '?'), false);
                        if (connectorId) refreshKnownLibraries(connectorId);
                    } else {
                        setStatus(statusEl, 'Failed: ' + (res.ErrorMessage || 'unknown error'), true);
                    }
                }
                function onError(e) {
                    console.error('[VirtualLib] Test error:', e);
                    setStatus(statusEl, 'Error: ' + e.message, true);
                }

                // For PlexTV with no machine selected yet, fall back to the saved-config endpoint.
                if (connType === 'PlexTV' && !machineId && connectorId) {
                    apiPost('/virtuallib/connectors/' + encodeURIComponent(connectorId) + '/test')
                        .then(onSuccess).catch(onError);
                    return;
                }

                var params = {
                    ServerType:            connType,
                    ServerUrl:             connType === 'PlexTV' ? '' : q('connectorUrl').value.trim(),
                    PlexMachineIdentifier: machineId,
                    AuthMode:              q('connectorAuthMode').value,
                    ApiKey:                q('connectorApiKey').value.trim(),
                    Username:              q('connectorUsername').value.trim(),
                    Password:              q('connectorPassword').value
                };

                console.log('[VirtualLib] Sending ad-hoc test with params:', JSON.stringify({
                    ServerType: params.ServerType,
                    PlexMachineIdentifier: params.PlexMachineIdentifier,
                    AuthMode: params.AuthMode,
                    ApiKey: params.ApiKey ? '(set)' : '(empty)'
                }));

                apiPost('/virtuallib/test-connection', params)
                    .then(onSuccess).catch(onError);
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
                var maxParallel  = parseInt(q('connectorMaxParallel').value, 10) || 4;

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
                            MaxParallelLibraries: maxParallel,
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
                        MaxParallelLibraries: maxParallel,
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
                startSync('/virtuallib/sync');
            });
        });
    };
});
