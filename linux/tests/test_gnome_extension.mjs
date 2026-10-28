import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const extensionSource = fs.readFileSync(path.join(root, 'gnome-extension', 'extension.js'), 'utf8');
const stylesheet = fs.readFileSync(path.join(root, 'gnome-extension', 'stylesheet.css'), 'utf8');

class FakeCancellable {
    cancelled = false;
    cancel() { this.cancelled = true; }
}

class FakeLabel {
    constructor({text = '', style_class = ''} = {}) {
        this.text = text;
        this.style_class = style_class;
        this.styleClasses = [];
        this.clutter_text = {line_wrap: null, line_wrap_mode: null, ellipsize: null};
    }

    add_style_class_name(name) { this.styleClasses.push(name); }
}

class FakePopupMenuItem {
    constructor(text) { this.label = new FakeLabel({text}); }
    connect(signal, callback) { this.signal = signal; this.callback = callback; return 1; }
}

class FakeMenu {
    items = [];
    addMenuItem(item) { this.items.push(item); }
}

class FakeButton {
    constructor() { this.menu = new FakeMenu(); this.children = []; this.destroyed = false; }
    add_child(child) { this.children.push(child); }
    destroy() { this.destroyed = true; }
}

class FakeExtension {
    uuid = 'agent-speed@test';
}

function loadExtension() {
    let proxy;
    let nextTimerId = 1;
    const timers = new Map();
    const statusArea = [];

    class FakeProxy {
        constructor(_connection, _name, _path, initCallback) {
            this.g_name_owner = ':1.42';
            this.calls = [];
            this.ownerChanged = null;
            this.disconnected = false;
            proxy = this;
            queueMicrotask(() => initCallback(this, null));
        }

        connect(signal, callback) {
            assert.equal(signal, 'notify::g-name-owner');
            this.ownerChanged = callback;
            return 7;
        }

        disconnect(handlerId) {
            assert.equal(handlerId, 7);
            this.disconnected = true;
        }

        GetSnapshotRemote(callback, cancellable) { this.calls.push({callback, cancellable}); }

        setOwner(owner) {
            this.g_name_owner = owner;
            this.ownerChanged?.();
        }
    }

    const GLib = {
        PRIORITY_DEFAULT: 0,
        SOURCE_CONTINUE: true,
        get_language_names: () => ['en_US.UTF-8'],
        timeout_add(_priority, _interval, callback) {
            const id = nextTimerId++;
            timers.set(id, callback);
            return id;
        },
        source_remove(id) { timers.delete(id); },
    };
    const Gio = {
        Cancellable: FakeCancellable,
        DBus: {session: {}},
        DBusProxy: {makeProxyWrapper: () => FakeProxy},
        DesktopAppInfo: {new: () => null},
    };
    const Pango = {EllipsizeMode: {END: 3}, WrapMode: {WORD_CHAR: 2}};
    const St = {Label: FakeLabel, Side: {TOP: 0}};
    const Main = {panel: {addToStatusArea: (_uuid, button) => statusArea.push(button)}};
    const PanelMenu = {Button: FakeButton};
    const PopupMenu = {PopupMenuItem: FakePopupMenuItem};

    const executableSource = extensionSource
        .replace(/^import .*;\s*$/gm, '')
        .replace('export default class AgentSpeed', 'class AgentSpeed') +
        '\nglobalThis.AgentSpeed = AgentSpeed;';
    const context = vm.createContext({Gio, GLib, Pango, St, Extension: FakeExtension, Main, PanelMenu, PopupMenu});
    vm.runInContext(executableSource, context, {filename: 'extension.js'});

    return {
        createExtension: () => new context.AgentSpeed(),
        timers,
        statusArea,
        get proxy() { return proxy; },
    };
}

test('bounds the panel label and keeps full long details in the menu', async () => {
    const runtime = loadExtension();
    const extension = runtime.createExtension();
    extension.enable();
    await Promise.resolve();

    const button = runtime.statusArea[0];
    const label = extension._label;
    assert.equal(runtime.proxy.calls.length, 1);
    assert.equal(label.clutter_text.line_wrap, false);
    assert.equal(label.clutter_text.ellipsize, 3);
    assert.match(stylesheet, /\.agent-speed-value\s*\{[^}]*min-width:\s*0\s*;/s);
    assert.match(stylesheet, /\.agent-speed-value\s*\{[^}]*max-width:\s*200px\s*;/s);

    const configuredLabel = `速度 2749.9 tok/s\n${'人民币 ¥1.23 · '.repeat(80)}今日 ¥45.67`;
    const firstRequest = runtime.proxy.calls[0];
    firstRequest.callback([JSON.stringify({
        schema: 1,
        speed: 2749.9,
        label: configuredLabel,
        agents: 3,
        cost: '人民币 ¥1.23',
        todayCost: '今日 ¥45.67',
        rx: 1234,
        tx: null,
        bits: false,
    })], null);

    assert.equal(label.text, configuredLabel.replace(/\s+/gu, ' ').trim());
    assert.equal(label.text.includes('\n'), false);
    assert.equal(extension._details.label.text.includes(configuredLabel), true);
    assert.match(extension._details.label.text, /3 agents · ↓ 1\.2 KB\/s  ↑ —/);
    assert.equal(extension._details.label.clutter_text.line_wrap, true);
    assert.equal(extension._details.label.clutter_text.line_wrap_mode, 2);
    assert.match(stylesheet, /\.agent-speed-details-value\s*\{[^}]*max-width:\s*360px\s*;/s);

    const timer = runtime.timers.get(1);
    timer();
    assert.equal(runtime.proxy.calls.length, 2, 'a completed update permits the next poll');
    runtime.proxy.calls[1].callback([JSON.stringify({
        schema: 1,
        speed: 10,
        label: '10 tok/s',
        agents: 1,
        cost: '$0.10',
        todayCost: '$1.00',
        rx: null,
        tx: null,
        bits: false,
    })], null);
    assert.match(extension._details.label.text, /Current · \$0\.10/);
    assert.match(extension._details.label.text, /Today · \$1\.00/);
    timer();
    assert.equal(runtime.proxy.calls.length, 3);
    timer();
    assert.equal(runtime.proxy.calls.length, 3, 'only one snapshot request may be in flight');

    extension.disable();
    assert.equal(button.destroyed, true);
});

test('cancels lost-owner requests, ignores stale replies, and survives disable/enable', async () => {
    const runtime = loadExtension();
    const extension = runtime.createExtension();
    extension.enable();
    await Promise.resolve();

    const first = runtime.proxy.calls[0];
    runtime.proxy.setOwner(null);
    assert.equal(first.cancellable.cancelled, true);
    assert.equal(extension._label.text, '— tok/s');

    runtime.proxy.setOwner(':1.43');
    assert.equal(runtime.proxy.calls.length, 2, 'owner return starts a fresh request immediately');
    first.callback([JSON.stringify({schema: 1, speed: 999, label: 'stale'})], null);
    assert.notEqual(extension._label.text, 'stale');
    runtime.proxy.calls[1].callback([JSON.stringify({schema: 1, speed: 8.5, label: '8.5 tok/s'})], null);
    assert.equal(extension._label.text, '8.5 tok/s');

    extension._poll();
    const replacedOwnerRequest = runtime.proxy.calls.at(-1);
    runtime.proxy.setOwner(':1.44');
    assert.equal(replacedOwnerRequest.cancellable.cancelled, true);
    const replacementRequest = runtime.proxy.calls.at(-1);
    assert.notEqual(replacementRequest, replacedOwnerRequest);
    replacedOwnerRequest.callback([JSON.stringify({schema: 1, speed: 999, label: 'old owner'})], null);
    assert.notEqual(extension._label.text, 'old owner');
    replacementRequest.callback([JSON.stringify({schema: 1, speed: 9, label: 'new owner'})], null);
    assert.equal(extension._label.text, 'new owner');

    extension._poll();
    const pendingBeforeDisable = runtime.proxy.calls.at(-1);
    const oldProxy = runtime.proxy;
    extension.disable();
    assert.equal(pendingBeforeDisable.cancellable.cancelled, true);
    assert.equal(oldProxy.disconnected, true);
    pendingBeforeDisable.callback([JSON.stringify({schema: 1, speed: 99, label: 'late'})], null);

    extension.enable();
    await Promise.resolve();
    assert.equal(runtime.proxy.calls.length, 1, 'enable creates a clean proxy and request state');
    runtime.proxy.calls[0].callback([JSON.stringify({schema: 1, speed: 12, label: '12 tok/s'})], null);
    assert.equal(extension._label.text, '12 tok/s');
    extension.disable();
});

test('resets visible state on a D-Bus method error and resumes polling', async () => {
    const runtime = loadExtension();
    const extension = runtime.createExtension();
    extension.enable();
    await Promise.resolve();

    runtime.proxy.calls[0].callback(null, new Error('service disconnected'));
    assert.equal(extension._label.text, '— tok/s');
    assert.equal(extension._details.label.text, 'Waiting for Agent Speed…');
    runtime.timers.get(1)();
    assert.equal(runtime.proxy.calls.length, 2);
    runtime.proxy.calls[1].callback([JSON.stringify({schema: 1, speed: 4.2, label: '4.2 tok/s'})], null);
    assert.equal(extension._label.text, '4.2 tok/s');
    extension.disable();
});
