# Agent group update rollouts

An administrator selects one trusted package and previews the enabled, deep-monitoring devices in a group. The preview includes enrolled Agents whose last reported runtime identifier matches that package; other eligible devices are excluded. Creation repeats the check and saves the resulting membership and device names as a snapshot. Later group membership changes do not alter an existing rollout.

The first `PilotSize` devices form batch zero. Later devices are divided into batches of at most `BatchSize`. The worker assigns devices in the current batch and waits for the highest-sequence result of each assignment. Only a reported success advances that device; an offline device remains waiting. A failed or replaced assignment pauses the rollout. A normal resume does not ignore failures. An administrator can explicitly choose to skip the affected devices and continue; this decision is audited, and the final rollout status indicates that issues occurred. Pausing or cancelling a rollout stops subsequent assignments; it does not retract updates already assigned.

Each assignment, its immutable target, audit record and rollout member transition are committed in one short transaction. A saved assignment revision prevents a group rollout from replacing a later manual assignment or a competing rollout. An existing unfinished assignment is left alone until a success or a non-retryable terminal result arrives. The worker scans running rollouts in keyset pages, so older offline rollouts do not prevent later rollouts from being considered.

The platform does not impose a global limit on the number of devices or outstanding Agent assignments. Each rollout uses the pilot and batch sizes chosen for that rollout.

The worker currently starts an eligible rollout at its next scheduled scan. User-selected maintenance windows, the complete end-to-end test with an official signed package, and the remaining REQ-20 acceptance items are not yet complete.
