#pragma once

class AppState;
class HistoryModel;

// Development aid (debug builds only): AGENT_SPEED_REMOTE_TRACE=1 prints, every 2 s, each remote host's status, agents
// and today's output from the snapshot, and the history rows per host whenever they change. Numbers and status codes
// only (REMOTE 4.1: never stderr, host names or paths).
void installRemoteTrace(AppState& state, HistoryModel& history);
