# NPC AI Design Direction

This document defines the product direction and non-negotiable design rules
for NPC intelligence in Island RPG. Implementation details may change, but new
NPC features should preserve these principles.

## Core fantasy

NPCs are people living in the same world as the player. They are not quest
dispensers, workforce units or followers waiting to be claimed. Every NPC has
their own incomplete knowledge, needs, relationships, values and plans.

The player enters an existing social world. They may become a friend, rival,
employee, follower, outsider or leader through play, but none of those roles is
assumed merely because they are the player.

## No privileged knowledge of the player

An NPC must not automatically know that a nearby actor is the player.

Before learning otherwise, the player is just an unknown person in the world.
An NPC should not know the player's name, history, skills, inventory,
intentions, reputation or importance unless that information was:

- directly observed;
- introduced in conversation;
- shared by another character;
- inferred from visible evidence; or
- remembered from an earlier encounter.

Systems must not leak player-only state into NPC decisions or dialogue. The
runtime may use a player identifier to route events, but NPC reasoning must use
an NPC-owned knowledge model. Unknown facts remain unknown, and beliefs may be
incomplete, outdated or wrong.

Recognition should be earned and persistent. An NPC can forget uncertain
details, confuse strangers, distrust a claim or recognize the player by prior
behavior without knowing their name.

## Every NPC is autonomous

Each NPC chooses actions according to their own needs, commitments,
relationships and circumstances. They can:

- accept, refuse, question or renegotiate a request;
- leave unsafe, unfair or unwanted situations;
- protect their own supplies and personal space;
- pursue work, rest, exploration and relationships without player input;
- disagree with the player and with other NPCs; and
- change priorities when new information or danger appears.

A direct player instruction is a social request, not an engine-level command.
Following it depends on trust, authority, incentives, risk, capability and the
NPC's current commitments.

NPC autonomy must not be implemented as random disobedience. Decisions should
have understandable causes that can be communicated through behavior,
conversation or inspectable intent.

## Any NPC may lead

Leadership is not reserved for the player. Any capable NPC may propose a plan,
organize work, recruit help, manage shared resources, settle disputes or lead a
group through danger.

Leadership should emerge from context such as:

- relevant skill or knowledge;
- earned trust and relationships;
- willingness to take responsibility;
- success or failure of previous plans;
- control of resources or a recognized social role; and
- support from other group members.

Different situations may produce different leaders. A skilled fisher may lead
a food expedition while another NPC coordinates defence. Leadership can be
contested, transferred, rejected or lost.

The player may support an NPC-led plan, negotiate an alternative, compete for
influence or leave. NPC-led activity must remain able to progress without the
player's attention.

## Work is cooperation, not ownership

Work roles such as Food, Wood, Crafting and Exploration describe a temporary
coordination need. They do not make an NPC property of the player or permanently
lock the NPC into a job.

Role assignment should eventually be expressed as a proposal or group
decision. An NPC considers suitability, urgency, fairness, safety, personal
goals and existing commitments before accepting. Roles must be revisited when
conditions change.

Personal items remain personal. Shared storage, tools and food require an
explicit group, household or settlement policy. Proximity to the player does
not grant ownership or permission.

## Social model

Relationships are directional. Mira may trust Rowan even when Rowan distrusts
Mira. NPCs track experiences with individuals rather than using one universal
attitude toward the player.

Important social state includes:

- identity knowledge and uncertainty;
- familiarity, trust, respect, fear and affection;
- promises, debts, grievances and witnessed behavior;
- group membership and recognized authority; and
- consent to follow, trade, share resources or cooperate.

Reputation is information transmitted through the social world, not a magical
global score. Claims can spread unevenly and may be challenged by direct
experience.

## Decision priority

NPC decision-making should generally resolve concerns in this order:

1. Immediate survival and self-defence.
2. Dependants, promises and urgent social responsibilities.
3. Current voluntary commitments and group plans.
4. Personal needs, goals and relationships.
5. New requests and opportunities.
6. Low-priority idle activity.

This is guidance rather than a rigid behavior tree. Personality, urgency and
context may change the result, but player requests do not receive an automatic
top priority.

## Player-facing clarity

Autonomous behavior must be legible. The player should be able to understand,
at an appropriate level, what an NPC is doing and why without seeing private
implementation data.

Useful presentation includes:

- current activity and immediate intent;
- stated reasons for accepting or refusing a request;
- visible group roles and active plans;
- relationship changes tied to observed events; and
- uncertainty in dialogue when the NPC lacks information.

Do not expose exact hidden scores when natural language or behavior would
communicate the same idea more convincingly.

## Engineering rules

New NPC systems should follow these constraints:

- NPC decisions consume observations and remembered beliefs, not unrestricted
  game state.
- Player identity is not a special fact in NPC memory; it is learned like any
  other actor identity.
- Shared action logic should work for players and NPCs where their physical
  capabilities are equivalent.
- Social permission and item ownership checks must apply consistently to all
  actors.
- Long-running NPC plans must persist and simulate without the player nearby.
- Deterministic gameplay services should be separated from rendering and AI
  narration.
- Important behavior needs scenario tests covering player and NPC initiators.

## Required scenario tests

The NPC test matrix should grow to cover at least these cases:

- a stranger does not know or use the player's name;
- an introduction changes identity knowledge for only the participants;
- information can be learned second-hand with a recorded source;
- an NPC refuses a dangerous or unfair request for a clear reason;
- an NPC proposes and leads a group activity;
- another NPC accepts or rejects that leadership independently;
- an NPC-led plan continues while the player is absent;
- work roles change without treating the player as the permanent assigner;
- personal and shared storage obey the same permission rules for every actor;
  and
- hostility, trust and reputation remain directional rather than global.

## Near-term implementation sequence

1. Add explicit actor identity knowledge: unknown, claimed, recognized and
   verified.
2. Remove dialogue and decisions that read the player's name or status before
   an NPC has learned them.
3. Represent requests as proposals with acceptance, refusal and reasons.
4. Replace automatic work assignment with group proposals and voluntary role
   commitments.
5. Add NPC-authored plans with a leader, participants, goal and lifecycle.
6. Introduce group membership and explicit personal/shared resource policies.
7. Expand observation tools and deterministic scenarios around NPC-led play.

## Design review question

Before approving an NPC feature, ask:

> Would this behavior still make sense if the initiating character were an NPC
> and the player were absent?

If the answer is no, the feature should be redesigned or its player-specific
exception should be explicit and justified.
