import http from 'k6/http';
import { fail } from 'k6';
import exec from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';
import { WebSocket } from 'k6/websockets';
import { clearInterval, setInterval, setTimeout } from 'k6/timers';

const BASE_URL = __ENV.BASE_URL || 'https://chathub.rehearsal.test';
const WS_URL = BASE_URL.replace(/^http/, 'ws');
const RATE = Number(__ENV.RATE || 20);
const DURATION = Number(__ENV.DURATION || 240);
const USERS = Number(__ENV.USERS || 60);
const LISTENERS = Number(__ENV.LISTENERS || 20);
const PROBE_SECONDS = Number(__ENV.PROBE_SECONDS || 5);
const WRITE_RATIO = Number(__ENV.WRITE_RATIO || 0.1);
const OUT = __ENV.OUT || '/out';
const RS = '\u001e';

const failedRequests = new Counter('failed_requests');
const okRequests = new Counter('ok_requests');
const byInstance = new Counter('responses_by_instance');
const wsHandshakes = new Counter('ws_handshakes');
const wsClosedByServer = new Counter('ws_closed_by_server');
const wsCloseMessages = new Counter('ws_close_messages');
const wsReconnects = new Counter('ws_reconnects');
const wsErrors = new Counter('ws_errors');
const probesSent = new Counter('probes_sent');
const probesReceived = new Counter('probes_received');
const reconnectMs = new Trend('ws_reconnect_ms', true);

export const options = {
    scenarios: {
        reads: {
            executor: 'constant-arrival-rate', exec: 'reads', rate: RATE, timeUnit: '1s', duration: `${DURATION}s`,
            preAllocatedVUs: RATE * 2, maxVUs: RATE * 10,
        },
        listeners: {
            executor: 'per-vu-iterations', exec: 'listen', vus: LISTENERS, iterations: 1, maxDuration: `${DURATION + 60}s`,
        },
    },
    thresholds: { failed_requests: ['count<1'] },
    insecureSkipTLSVerify: true,
    discardResponseBodies: true,
    setupTimeout: '3m',
    summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

const emailOf = (index) => `seed-${String(index).padStart(4, '0')}@chathub.test`;
const json = { 'Content-Type': 'application/json' };
const auth = (token, endpoint) => ({ headers: { ...json, Authorization: `Bearer ${token}` }, tags: { endpoint } });

function record(response, endpoint, expected) {
    const instance = response.headers['X-Instance-Id'] || 'none';
    byInstance.add(1, { instance });
    if (response.status === expected) {
        okRequests.add(1, { endpoint });
        return true;
    }
    failedRequests.add(1, { endpoint, status: String(response.status) });
    console.warn(`FAILED ${endpoint} -> ${response.status} ${response.error || ''} at ${new Date().toISOString()}`);
    return false;
}

export function setup() {
    const users = [];
    for (let i = 0; i < USERS; i++) {
        const index = 1 + Math.floor((i * 1000) / USERS);
        const login = http.post(`${BASE_URL}/api/auth/login`, JSON.stringify({ identifier: emailOf(index), password: 'Seed12345' }),
            { headers: json, responseType: 'text' });
        if (login.status !== 200) fail(`login ${emailOf(index)} failed with ${login.status}`);
        const token = login.json().accessToken;
        const page = http.get(`${BASE_URL}/api/conversations?pageSize=20`, { ...auth(token, 'setup'), responseType: 'text' });
        if (page.status !== 200) fail(`conversation list failed with ${page.status}`);
        const conversations = page.json().items.map((item) => item.id);
        if (conversations.length > 0) users.push({ token, conversations });
    }
    if (users.length < LISTENERS) fail(`only ${users.length} users have conversations`);
    console.log(`SETUP_DONE users=${users.length}`);
    return { users };
}

const pick = (values) => values[Math.floor(Math.random() * values.length)];

export function reads(data) {
    const user = pick(data.users);
    const conversationId = pick(user.conversations);
    record(http.get(`${BASE_URL}/api/users/me`, auth(user.token, 'me')), 'me', 200);
    record(http.get(`${BASE_URL}/api/conversations`, auth(user.token, 'conversations')), 'conversations', 200);
    record(http.get(`${BASE_URL}/api/conversations/${conversationId}/messages`, auth(user.token, 'messages')), 'messages', 200);
    if (Math.random() < WRITE_RATIO) {
        record(http.post(`${BASE_URL}/api/conversations/${conversationId}/messages`,
            JSON.stringify({ type: 'Text', content: `rehearsal ${exec.scenario.iterationInTest}` }), auth(user.token, 'send')), 'send', 201);
    }
}

export function listen(data) {
    const index = exec.scenario.iterationInTest;
    const user = data.users[index];
    const conversationId = user.conversations[0];
    let socket = null;
    let open = false;
    let stopping = false;
    let everOpened = false;
    let lostAt = 0;
    let sequence = 0;

    const connect = () => {
        open = false;
        socket = new WebSocket(`${WS_URL}/hubs/chat?access_token=${encodeURIComponent(user.token)}`);
        socket.onopen = () => socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
        socket.onmessage = (event) => {
            for (const frame of String(event.data).split(RS)) {
                if (!frame) continue;
                const message = JSON.parse(frame);
                if (!open && message.type === undefined) {
                    if (message.error) {
                        wsErrors.add(1);
                        continue;
                    }
                    open = true;
                    wsHandshakes.add(1);
                    if (everOpened) {
                        wsReconnects.add(1);
                        reconnectMs.add(Date.now() - lostAt);
                    }
                    everOpened = true;
                    continue;
                }
                if (message.type === 7) wsCloseMessages.add(1);
                if (message.type === 1 && message.target === 'MessageReceived') {
                    const content = message.arguments[0].content || '';
                    if (content.startsWith(`probe:${index}:`)) probesReceived.add(1);
                }
            }
        };
        socket.onclose = () => {
            open = false;
            if (stopping) return;
            wsClosedByServer.add(1);
            lostAt = Date.now();
            console.warn(`ws ${index} closed at ${new Date().toISOString()}, reconnecting`);
            setTimeout(connect, 200 + Math.random() * 800);
        };
        socket.onerror = (event) => {
            if (stopping) return;
            wsErrors.add(1);
            console.warn(`ws ${index} error: ${event.error}`);
        };
    };

    connect();
    const ping = setInterval(() => {
        if (open) socket.send(JSON.stringify({ type: 6 }) + RS);
    }, 10000);
    const probe = setInterval(() => {
        if (!open) return;
        const sent = http.post(`${BASE_URL}/api/conversations/${conversationId}/messages`,
            JSON.stringify({ type: 'Text', content: `probe:${index}:${sequence++}` }), auth(user.token, 'probe'));
        if (record(sent, 'probe', 201)) probesSent.add(1);
    }, PROBE_SECONDS * 1000);
    setTimeout(() => {
        stopping = true;
        clearInterval(ping);
        clearInterval(probe);
        setTimeout(() => socket.close(), 3000);
    }, DURATION * 1000);
}

const count = (data, name) => (data.metrics[name] ? data.metrics[name].values.count : 0);

export function handleSummary(data) {
    const instances = Object.keys(data.metrics)
        .filter((name) => name.startsWith('responses_by_instance{'))
        .map((name) => `${name.slice('responses_by_instance{instance:'.length, -1)}=${data.metrics[name].values.count}`);
    const failures = Object.keys(data.metrics)
        .filter((name) => name.startsWith('failed_requests{'))
        .map((name) => `${name}=${data.metrics[name].values.count}`);
    const reconnect = data.metrics.ws_reconnect_ms ? data.metrics.ws_reconnect_ms.values : {};
    const summary = {
        httpOk: count(data, 'ok_requests'),
        httpFailed: count(data, 'failed_requests'),
        failures,
        instances,
        wsHandshakes: count(data, 'ws_handshakes'),
        wsClosedByServer: count(data, 'ws_closed_by_server'),
        wsCloseMessages: count(data, 'ws_close_messages'),
        wsReconnects: count(data, 'ws_reconnects'),
        wsErrors: count(data, 'ws_errors'),
        reconnectMs: { med: reconnect.med, p95: reconnect['p(95)'], max: reconnect.max },
        probesSent: count(data, 'probes_sent'),
        probesReceived: count(data, 'probes_received'),
        httpP95Ms: data.metrics.http_req_duration ? data.metrics.http_req_duration.values['p(95)'] : undefined,
    };
    const text = `\n=== rolling rehearsal ===\n${JSON.stringify(summary, null, 2)}\n`;
    return { [`${OUT}/${__ENV.RESULT_NAME || 'rolling'}.summary.json`]: JSON.stringify(summary, null, 2), stdout: text };
}
