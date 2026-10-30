#!/usr/bin/env python3
"""Contract tests for optional report fields and independently signed multi-device profiles."""

from __future__ import annotations

import copy
import json
import struct
import unittest
import uuid
import zlib

import execution_modes_reference as modes
import report_reference as ref
import report_perf_score as perf_score
from report_profile_reference import PNG_MAGIC, sanitize_avatar


def png(metadata=False):
    def chunk(tag, value):
        return struct.pack(">I", len(value)) + tag + value + struct.pack(">I", zlib.crc32(tag + value) & 0xffffffff)
    header = chunk(b"IHDR", struct.pack(">IIBBBBB", 256, 256, 8, 6, 0, 0, 0))
    ancillary = chunk(b"tEXt", b"Location\0must-not-survive") if metadata else b""
    pixels = (b"\0" + b"\x40\x80\xc0\xff" * 256) * 256
    return PNG_MAGIC + header + ancillary + chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b"")


class ContractTests(unittest.TestCase):
    def test_shared_mode_vectors(self):
        self.assertEqual(modes.check_fixtures(), 191)

    def test_modes_and_harness_must_conserve_parent_totals(self):
        model = {"sources": ["codex", "opencode"], "requests": 3, "output": 30, "measuredOutput": 20,
                 "measuredMs": 1000, "harnessRequests": [{"source": "codex", "requests": 1},
                                                        {"source": "opencode", "requests": 2}],
                 "modeUsage": [{"source": "codex", "kind": "serviceTier", "mode": "unknown", "evidence": "unknown",
                                "requests": 1, "output": 10, "measuredOutput": 0, "measuredMs": 0},
                               {"source": "opencode", "provider": "openai", "kind": "serviceTier", "mode": "fast",
                                "evidence": "served", "requests": 2, "output": 20, "measuredOutput": 20,
                                "measuredMs": 1000}]}
        self.assertEqual(modes.model_extension_errors(model), [])
        for mutation in ("harnessRequests", "modeUsage"):
            broken = copy.deepcopy(model)
            broken[mutation][0]["requests"] += 1
            self.assertTrue(modes.model_extension_errors(broken), mutation)
        broken = copy.deepcopy(model)
        broken["modeUsage"].append(copy.deepcopy(broken["modeUsage"][0]))
        self.assertTrue(any("duplicate" in reason for _, reason in modes.model_extension_errors(broken)))

    def test_effort_rows_need_version_2_and_no_provider(self):
        row = {"source": "codex", "kind": "effort", "mode": "xhigh", "evidence": "recorded",
               "requests": 3, "output": 30, "measuredOutput": 20, "measuredMs": 1000}
        model = {"sources": ["codex"], "requests": 3, "output": 30, "measuredOutput": 20, "measuredMs": 1000,
                 "modeUsage": [row]}
        self.assertEqual(modes.model_extension_errors(model, extensions={"executionModes": 2}), [])
        self.assertIn(("/models/0/modeUsage/0/kind", "unknown kind"),
                      modes.model_extension_errors(model, extensions={"executionModes": 1}))
        model["modeUsage"][0] = {**row, "provider": "openai"}
        self.assertIn(("/models/0/modeUsage/0/provider", "effort rows have no provider"),
                      modes.model_extension_errors(model, extensions={"executionModes": 2}))

    def test_discovery_keeps_the_execution_modes_version(self):
        for advertised in (1, 2):
            service = ref.ReferenceService(authority="127.0.0.1:18789", extensions={"executionModes": advertised})
            parsed = ref.parse_discovery(service.discovery("http"), "http://127.0.0.1:18789/.well-known/agent-speed-report")
            self.assertEqual(parsed["extensions"]["executionModes"], advertised)
        service = ref.ReferenceService(authority="127.0.0.1:18789", extensions={"executionModes": 3})
        self.assertNotIn("extensions", service.discovery("http"))

    def test_untrusted_sources_and_unsafe_numbers_are_rejected(self):
        for source in ({}, [], True, None):
            model = {"sources": ["codex"], "requests": 0,
                     "harnessRequests": [{"source": source, "requests": True}]}
            self.assertTrue(modes.model_extension_errors(model))

    def test_legacy_receiver_ignores_unadvertised_optional_semantics(self):
        model = {"sources": ["codex"], "requests": 3,
                 "harnessRequests": [{"source": "codex", "requests": 99}]}
        self.assertEqual(modes.model_extension_errors(model, extensions={}), [])

    def test_avatar_metadata_removed_and_stream_bounded(self):
        clean = sanitize_avatar(png(metadata=True))
        self.assertNotIn(b"must-not-survive", clean)
        self.assertEqual(sanitize_avatar(clean), clean)
        for invalid in (b"<svg></svg>", png()[:-1], b"x" * (512 * 1024 + 1)):
            with self.assertRaises(ValueError):
                sanitize_avatar(invalid)


class ProfileTests(unittest.TestCase):
    def setUp(self):
        self.now = 1800000000.0
        self.service = ref.ReferenceService(authority="127.0.0.1:18789", extensions={k: 1 for k in modes.EXTENSIONS})
        self.ids = ["11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222",
                    "33333333-3333-4333-8333-333333333333"]
        self.keys = [ref.test_private_key("extension-device-" + str(i)) for i in range(3)]
        self.names = ["TestMac", "TestWin", "TestOther"]
        for i in range(3):
            self.assertEqual(self.call(i, "POST", "/v1/devices", {
                "schema": 1, "deviceId": self.ids[i], "username": self.names[i], "publicKey": ref.jwk(ref.public_key(self.keys[i])),
                "client": {"app": "AgentSpeed", "version": "1.3.0", "platform": "macOS" if i == 0 else "Windows",
                           "osMajor": 14 if i == 0 else 11}})[0], 201)

    def call(self, index, method, path, document=None, *, raw=None, content_type=None):
        self.now += 2
        body = raw if raw is not None else (ref.compact_json(document) if document is not None else b"")
        headers = ref.signed_headers(self.keys[index], method, "http://127.0.0.1:18789" + path, self.ids[index], body,
                                     timestamp=int(self.now))
        if content_type:
            headers["Content-Type"] = content_type
        return self.service.handle(method, path, headers, body, now=self.now)

    def pair(self):
        profile = self.call(0, "POST", "/v1/profile/identity", {})
        self.assertEqual(profile[0], 201)
        invite = self.call(0, "POST", "/v1/profile/pairings", {})
        self.assertEqual(invite[0], 201)
        pair = invite[2]
        claimed = self.call(1, "POST", "/v1/profile/pairings/claim", {"code": pair["code"].lower()})
        self.assertEqual(claimed[0], 202)
        self.assertNotIn("code", claimed[2])
        self.assertNotIn(self.ids[1], self.service.extension_state.bindings)
        return profile[2], pair

    def approved_pair(self):
        profile, pair = self.pair()
        self.assertEqual(self.call(0, "POST", "/v1/profile/pairings/" + pair["id"] + "/approve", {})[0], 200)
        return profile, pair

    def report(self, index, seq=1, speed=100, requests=1, run=None):
        document = copy.deepcopy(ref.load_examples()["report-active"])
        document["deviceId"], document["username"] = self.ids[index], self.names[index]
        document["sentAt"] = ref.format_instant(int((self.now + 2) * 1000))
        document["run"] = {"id": run or str(uuid.uuid4()), "seq": seq}
        document["live"]["outputTokS"] = speed
        document["peak"]["outputTokS"] = max(speed, document["peak"]["outputTokS"])
        return document

    def test_creator_approval_and_independent_keys(self):
        profile, pair = self.pair()
        endpoint = "/v1/profile/pairings/" + pair["id"]
        self.assertEqual(self.call(2, "GET", endpoint)[0], 404)
        self.assertEqual(self.call(1, "POST", endpoint + "/approve", {})[0], 404)
        self.assertEqual(self.call(0, "POST", endpoint + "/approve", {})[2]["state"], "approved")
        self.assertEqual(self.call(0, "POST", endpoint + "/approve", {})[0], 200)
        current = self.call(1, "GET", "/v1/profile")[2]
        self.assertEqual(current["sharedUserId"], profile["sharedUserId"])
        self.assertEqual(len(current["devices"]), 2)
        self.assertNotEqual(self.service.devices[self.ids[0]]["pub"], self.service.devices[self.ids[1]]["pub"])

    def test_claim_retry_after_approval_returns_same_binding(self):
        profile, pair = self.approved_pair()
        retry = self.call(1, "POST", "/v1/profile/pairings/claim", {"code": pair["code"]})
        self.assertEqual(retry[0], 202)
        self.assertEqual(retry[2]["id"], pair["id"])
        self.assertEqual(retry[2]["state"], "approved")
        self.assertNotIn("code", retry[2])
        self.assertEqual(self.service.extension_state.bindings[self.ids[1]], profile["sharedUserId"])
        self.call(1, "DELETE", "/v1/profile/identity")
        self.call(1, "POST", "/v1/profile/identity", {})
        self.assertEqual(self.call(1, "POST", "/v1/profile/pairings/claim", {"code": pair["code"]})[0], 409)

    def test_legacy_username_is_not_overwritten(self):
        self.approved_pair()
        self.assertEqual(self.call(1, "PATCH", "/v1/profile", {"displayName": "shared-name"})[0], 200)
        self.assertEqual(self.service.devices[self.ids[1]]["username"], self.names[1])
        self.assertEqual(self.call(0, "GET", "/v1/profile")[2]["displayName"], "shared-name")

    def test_arbitrary_target_identity_is_rejected(self):
        self.assertEqual(self.call(1, "POST", "/v1/profile/identity", {"sharedUserId": self.ids[0]})[0], 422)
        self.assertNotIn(self.ids[1], self.service.extension_state.bindings)

    def test_pair_expiry_and_unlinked_creator(self):
        _, pair = self.pair()
        self.now += 601
        self.assertEqual(self.call(0, "POST", "/v1/profile/pairings/" + pair["id"] + "/approve", {})[0], 410)
        self.assertNotIn(self.ids[1], self.service.extension_state.bindings)
        invite = self.call(0, "POST", "/v1/profile/pairings", {})[2]
        self.assertEqual(self.call(1, "POST", "/v1/profile/pairings/claim", {"code": invite["code"]})[0], 202)
        self.call(0, "DELETE", "/v1/profile/identity")
        self.assertEqual(self.call(0, "POST", "/v1/profile/pairings/" + invite["id"] + "/approve", {})[0], 409)

    def test_cross_device_avatar_and_removal(self):
        self.approved_pair()
        result = self.call(0, "PUT", "/v1/profile/avatar", raw=png(metadata=True), content_type="image/png")
        self.assertEqual(result[0], 200)
        blob = self.call(1, "GET", "/v1/profile/avatar")
        self.assertEqual(blob[0], 200)
        self.assertEqual(blob[1]["Content-Type"], "image/png")
        self.assertEqual(ref.sha256_hex(blob[2]), result[2]["avatar"]["sha256"])
        self.assertNotIn(b"must-not-survive", blob[2])
        path = "/public/avatars/" + result[2]["avatar"]["sha256"] + ".png"
        self.assertEqual(self.service.handle("GET", path, {}, b"", now=self.now)[2], blob[2])
        self.assertIsNone(self.call(1, "DELETE", "/v1/profile/avatar")[2]["avatar"])
        self.assertEqual(self.call(0, "GET", "/v1/profile/avatar")[0], 404)
        self.assertEqual(self.service.handle("GET", path, {}, b"", now=self.now)[0], 404)

    def test_live_sum_unlink_history_and_duplicate_attribution(self):
        profile, _ = self.approved_pair()
        reports = [self.report(0, speed=100), self.report(1, speed=150)]
        for index, document in enumerate(reports):
            self.assertEqual(self.call(index, "POST", "/v1/reports", document)[2]["status"], "accepted")
        def shared_row():
            return next(row for row in self.service.extension_state.public_users(self.now)
                        if row["id"] == self.service.extension_state.public_id(("user", profile["sharedUserId"])))
        total = shared_row()["totals"]["requests"]
        self.assertEqual(shared_row()["onlineDevices"], 2)
        self.assertEqual(shared_row()["live"]["outputTokS"], 250)
        self.assertEqual(self.call(1, "POST", "/v1/reports", reports[1])[2]["status"], "duplicate")
        self.assertEqual(shared_row()["totals"]["requests"], total)
        self.call(1, "DELETE", "/v1/profile/identity")
        self.assertEqual(shared_row()["live"]["outputTokS"], 100)
        self.assertEqual(shared_row()["onlineDevices"], 1)
        self.assertEqual(shared_row()["totals"]["requests"], total)
        self.assertEqual(self.call(1, "POST", "/v1/reports", reports[1])[2]["status"], "duplicate")
        self.assertEqual(shared_row()["totals"]["requests"], total)
        newer = self.report(1, speed=180)
        self.assertEqual(self.call(1, "POST", "/v1/reports", newer)[2]["status"], "accepted")
        self.assertEqual(shared_row()["totals"]["requests"], total)
        self.assertEqual(self.service.extension_state.bindings[self.ids[0]], profile["sharedUserId"])

    def test_invalid_extension_does_not_consume_sequence(self):
        document = self.report(0)
        model = document["models"][0]
        model["harnessRequests"] = [{"source": model["sources"][0], "requests": model["requests"] + 1}]
        self.assertEqual(self.call(0, "POST", "/v1/reports", document)[0], 422)
        key = (self.ids[0], document["run"]["id"], document["run"]["seq"])
        self.assertNotIn(key, self.service.seqs)
        model["harnessRequests"][0]["requests"] -= 1
        self.assertEqual(self.call(0, "POST", "/v1/reports", document)[2]["status"], "accepted")

    def test_signed_body_and_nonce_are_enforced(self):
        self.now += 2
        body = ref.compact_json({})
        headers = ref.signed_headers(self.keys[0], "POST", "http://127.0.0.1:18789/v1/profile/identity",
                                     self.ids[0], body, timestamp=int(self.now))
        self.assertEqual(self.service.handle("POST", "/v1/profile/identity", headers, b'{"x":1}', now=self.now)[0], 401)
        self.assertEqual(self.service.handle("POST", "/v1/profile/identity", headers, body, now=self.now)[0], 201)
        self.assertEqual(self.service.handle("POST", "/v1/profile/identity", headers, body, now=self.now)[2]["error"]["code"], "nonce_replayed")

    def test_delete_one_device_keeps_other_device_and_usage(self):
        profile, _ = self.approved_pair()
        for index in (0, 1):
            self.assertEqual(self.call(index, "POST", "/v1/reports", self.report(index))[0], 200)
        self.assertEqual(self.call(1, "DELETE", "/v1/devices/" + self.ids[1])[0], 200)
        self.assertIn(self.ids[0], self.service.devices)
        self.assertEqual(self.service.extension_state.bindings[self.ids[0]], profile["sharedUserId"])
        shared = next(row for row in self.service.extension_state.public_users(self.now)
                      if row["id"] == self.service.extension_state.public_id(("user", profile["sharedUserId"])))
        self.assertEqual(shared["onlineDevices"], 1)
        self.assertEqual(shared["totals"]["requests"], self.service.devices[self.ids[0]]["totals"]["requests"])

    def test_public_rows_do_not_expose_device_ids(self):
        self.assertEqual(self.call(0, "POST", "/v1/reports", self.report(0))[0], 200)
        payload = self.service.handle("GET", "/public/users", {}, b"", now=self.now)
        self.assertEqual(payload[0], 200)
        public_json = json.dumps(payload[2])
        for device_id in self.ids:
            self.assertNotIn(device_id, public_json)

    def test_legacy_and_new_mode_reports_keep_complete_unknown_denominators(self):
        self.approved_pair()
        reports = [self.report(0), self.report(1), self.report(0)]
        for kind, document in zip(("serviceTier", "routing"), (reports[0], reports[2])):
            for model in document["models"]:
                model["sources"] = ["codex", "opencode"]
                model["modeUsage"] = [{"source": "opencode", "provider": "openai", "kind": kind,
                                       "mode": "fast", "evidence": "served",
                                       **{field: model[field] for field in modes.FIELDS}}]
        for model in reports[1]["models"]:
            model["sources"] = ["codex", "opencode"]
        for index, document in zip((0, 1, 0), reports):
            self.assertEqual(self.call(index, "POST", "/v1/reports", document)[0], 200)
        rows = self.service.extension_state.public_users(self.now)
        for model in rows[0]["models"]:
            for kind in ("serviceTier", "routing"):
                parts = [row for row in model["modeUsage"] if row["kind"] == kind]
                for field in modes.FIELDS:
                    self.assertEqual(sum(row[field] for row in parts), model[field])
                unknown = next(row for row in parts if row["mode"] == "unknown")
                self.assertEqual(unknown["sources"], ["codex", "opencode"])
                self.assertNotIn("source", unknown)
                self.assertEqual(unknown["requests"], model["requests"] * 2 // 3)

    def test_future_source_order_is_validated_without_crashing(self):
        document = copy.deepcopy(ref.load_examples()["report-active"])
        for model in document["models"]:
            model["sources"] = ["futureHarness"]
        self.assertEqual(ref.report_errors(document, self.service.schema), [])


class PerfScoreTests(unittest.TestCase):
    """REPORTING-EXTENSIONS 9 / METRICS 11.10: the perfScore extension on the reference service."""

    def setUp(self):
        self.now = 1792656931.0
        self.ids = ["11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"]
        self.keys = [ref.test_private_key("perf-score-device-" + str(i)) for i in range(2)]
        self.names = ["PerfMac", "PerfWin"]

    def service(self, extensions=("perfScore",)):
        service = ref.ReferenceService(authority="127.0.0.1:18789",
                                       extensions={name: 1 for name in extensions} or None)
        for i in range(2):
            self.assertEqual(self.call(service, i, "POST", "/v1/devices", {
                "schema": 1, "deviceId": self.ids[i], "username": self.names[i],
                "publicKey": ref.jwk(ref.public_key(self.keys[i])),
                "client": {"app": "AgentSpeed", "version": "1.6.0", "platform": "macOS", "osMajor": 27}})[0], 201)
        return service

    def call(self, service, index, method, path, document=None):
        self.now += 5
        body = ref.compact_json(document) if document is not None else b""
        headers = ref.signed_headers(self.keys[index], method, "http://127.0.0.1:18789" + path, self.ids[index],
                                     body, timestamp=int(self.now))
        return service.handle(method, path, headers, body, now=self.now)

    def report(self, index, seq, run="c3d4e5f6-a7b8-4c9d-8e0f-1a2b3c4d5e6f", example="report-perfscore"):
        document = copy.deepcopy(ref.load_examples()[example])
        document.update(deviceId=self.ids[index], username=self.names[index], run={"id": run, "seq": seq},
                        sentAt=ref.format_instant(int((self.now + 5) * 1000)))
        return document

    def models(self, service):
        status, _, payload = service.handle("GET", "/public/models", {}, b"", now=self.now)
        self.assertEqual(status, 200)
        return {row["id"]: row for row in payload["models"]}, payload

    def test_stored_vectors_pass(self):
        vectors = json.loads(ref.VECTORS_PATH.read_text(encoding="utf-8"))
        self.assertEqual(perf_score.check_vectors(vectors["perfScore"]), [])
        self.assertEqual(perf_score.hand_check(), [])

    def test_rows_count_once_pool_across_devices_and_live_values_follow_the_latest_report(self):
        service = self.service()
        first = self.report(0, 1)
        self.assertEqual(self.call(service, 0, "POST", "/v1/reports", first)[2]["status"], "accepted")
        self.assertEqual(self.call(service, 0, "POST", "/v1/reports", first)[2]["status"], "duplicate")
        rows, payload = self.models(service)
        self.assertEqual((rows["claude-opus-5-5"]["n"], rows["claude-opus-5-5"]["tools"]), (3, 4))
        self.assertIsNone(rows["claude-opus-5-5"]["score"])          # 3 responses: below the 20 a score needs
        self.assertEqual(payload["live"], {"score": 1286, "form": "live", "thinkingTokS": 38.6})
        for seq in range(2, 8):
            self.assertEqual(self.call(service, 0, "POST", "/v1/reports", self.report(0, seq))[2]["status"],
                             "accepted")
        other = self.report(1, 1, run="d4e5f6a7-b8c9-4d0e-8f1a-2b3c4d5e6f70")
        self.assertEqual(self.call(service, 1, "POST", "/v1/reports", other)[2]["status"], "accepted")
        rows, payload = self.models(service)
        opus = rows["claude-opus-5-5"]
        self.assertEqual((opus["n"], opus["tools"], opus["ranked"]), (24, 32, True))
        # 8 rows of 3 responses, 1400 tokens in 3.3 s each: T = 11 200 / 26.4 s.
        self.assertAlmostEqual(opus["components"]["T"]["measure"], 11200 / 26.4)
        self.assertAlmostEqual(opus["score"], perf_score.pooled([row for _ in range(8) for row in
                                                                 first["models"][0]["perfStats"]])["score"])
        self.assertEqual(payload["live"]["form"], "live")
        self.assertEqual(self.call(service, 1, "DELETE", "/v1/devices/" + self.ids[1])[0], 200)
        rows, _ = self.models(service)
        self.assertEqual(rows["claude-opus-5-5"]["n"], 21)

    def test_broken_rows_are_rejected_without_using_the_sequence(self):
        service = self.service()
        document = self.report(0, 7)
        document["models"][0]["perfStats"][0]["toolFailed"] = 5
        status, _, payload = self.call(service, 0, "POST", "/v1/reports", document)
        self.assertEqual((status, payload["error"]["code"], payload["error"]["field"]),
                         (422, "payload_invalid", "/models/0/perfStats/0/toolFailed"))
        self.assertNotIn((self.ids[0], document["run"]["id"], 7), service.seqs)
        document["models"][0]["perfStats"][0]["toolFailed"] = 0
        self.assertEqual(self.call(service, 0, "POST", "/v1/reports", document)[2]["status"], "accepted")

    def test_unadvertised_service_ignores_the_members(self):
        service = self.service(extensions=())
        document = self.report(0, 1)
        document["perfScore"] = {"score": "high"}
        document["models"][0]["perfStats"] = "many"
        self.assertEqual(self.call(service, 0, "POST", "/v1/reports", document)[2]["status"], "accepted")
        self.assertEqual(service.handle("GET", "/public/models", {}, b"", now=self.now)[0], 404)
        self.assertEqual(modes.supported(service.discovery().get("extensions"), "perfScore"), False)

    def test_legacy_task_score_service_accepts_the_thinking_only_form(self):
        service = self.service(extensions=("taskScore",))
        legacy = self.report(0, 1, example="report-taskscore")
        self.assertEqual(legacy["taskScore"]["score"], None)
        self.assertEqual(self.call(service, 0, "POST", "/v1/reports", legacy)[2]["status"], "accepted")
        mixed = self.report(0, 2)
        mixed["perfScore"] = {"score": "ignored"}
        self.assertEqual(self.call(service, 0, "POST", "/v1/reports", mixed)[2]["status"], "accepted")

    def test_discovery_advertises_exactly_the_integer_one(self):
        self.assertEqual(self.service().discovery()["extensions"], {"perfScore": 1})
        parsed = ref.parse_discovery(self.service().discovery("http"),
                                     "http://127.0.0.1:18789/.well-known/agent-speed-report")
        self.assertTrue(perf_score.active(parsed))
        self.assertFalse(perf_score.active(parsed, models_switch=False))
        both = ref.parse_discovery(self.service(("perfScore", "taskScore")).discovery("http"),
                                   "http://127.0.0.1:18789/.well-known/agent-speed-report")
        self.assertEqual(perf_score.extension_sent(both), "perfScore")

    def test_public_models_do_not_expose_device_ids(self):
        service = self.service()
        self.assertEqual(self.call(service, 0, "POST", "/v1/reports", self.report(0, 1))[0], 200)
        public_json = json.dumps(self.models(service)[1])
        for device_id in self.ids:
            self.assertNotIn(device_id, public_json)


if __name__ == "__main__":
    unittest.main()
