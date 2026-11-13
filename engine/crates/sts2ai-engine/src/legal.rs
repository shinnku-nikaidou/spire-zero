//! The plays the game allows at a combat decision, and where the combat stands: `CombatActions.Legal` of
//! src/Sts2Ai.GameBridge/Actions/CombatActions.cs and `NodeWorld.Status` / `NodeWorld.Legal` of the simulator
//! (src/Sts2Ai.SimWorker/Simulation/NodeWorld.cs), in their order.

use crate::r#gen as g;
use crate::r#gen::enums::*;
use crate::r#gen::kinds::*;
use crate::protocol::CombatAction;
use crate::rt::{self, Combat, Obj};

/// C#: CombatStatus, as `sz_world_status` gives it.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Status {
    InProgress = 0,
    Won = 1,
    Lost = 2,
}

/// C#: NodeWorld.Status. Lost with the player dead, else in progress while the game says its combat is, else won.
pub fn status(player: Player, c: &mut Combat) -> Status {
    let me = g::entities_players::player::creature(player, c);
    if g::entities_creatures::creature::is_dead(me, c) {
        return Status::Lost;
    }
    let manager = g::combat::combat_manager::instance(c);
    if g::combat::combat_manager::is_in_progress(manager, c) {
        Status::InProgress
    } else {
        Status::Won
    }
}

/// C#: NodeWorld.Legal. The plays the game allows: none once the combat is over, and none at a card prompt, whose
/// answers are the actions then.
pub fn plays(st: CombatState, player: Player, at_prompt: bool, c: &mut Combat) -> Vec<CombatAction> {
    if status(player, c) != Status::InProgress || at_prompt {
        return Vec::new();
    }
    legal(st, player, c)
}

/// C#: CombatActions.Legal. Every legal action, cards in hand order first, then potions by slot, then ending the turn.
pub fn legal(st: CombatState, player: Player, c: &mut Combat) -> Vec<CombatAction> {
    let mut actions = Vec::new();
    let pcs = g::entities_players::player::player_combat_state(player, c);
    if rt::is_null(&pcs) {
        panic!("player has no combat state");
    }
    let hand = g::entities_players::player_combat_state::hand(pcs, c);
    for card in g::entities_cards::card_pile::cards(hand, c) {
        if !g::models::card_model::can_play__0(card, c) {
            continue;
        }
        let db = g::game_actions_multiplayer::net_combat_card_db::instance(c);
        let card_uid = g::game_actions_multiplayer::net_combat_card_db::get_card_id(db, card, c);
        let target_type = g::dispatch::card_model::target_type(card, c);
        if target_type == TargetType::AnyEnemy {
            for e in g::combat::combat_state::enemies(st, c) {
                if g::models::card_model::is_valid_target(card, e, c) {
                    actions.push(CombatAction::Play {
                        card: card_uid,
                        target: Some(uid(e, c)),
                    });
                }
            }
        } else if target_type == TargetType::AnyAlly {
            for a in g::combat::combat_state::allies(st, c) {
                if g::models::card_model::is_valid_target(card, a, c) {
                    actions.push(CombatAction::Play {
                        card: card_uid,
                        target: Some(uid(a, c)),
                    });
                }
            }
        } else if g::models::card_model::is_valid_target(card, Obj::NULL, c) {
            actions.push(CombatAction::Play {
                card: card_uid,
                target: None,
            });
        }
    }

    if g::entities_players::player::can_use_or_remove_potions(player, c) {
        let slots = g::entities_players::player::potion_slots(player, c);
        for (s, potion) in slots.into_iter().enumerate() {
            let slot = s as i32;
            if rt::is_null(&potion)
                || g::models::potion_model::is_queued(potion, c)
                || !g::dispatch::potion_model::passes_custom_usability_check(potion, c)
            {
                continue;
            }
            let usage = g::dispatch::potion_model::usage(potion, c);
            if !(usage == PotionUsage::CombatOnly || usage == PotionUsage::AnyTime) {
                continue;
            }
            let target_type = g::dispatch::potion_model::target_type(potion, c);
            if target_type == TargetType::AnyEnemy {
                for e in g::combat::combat_state::enemies(st, c) {
                    if g::entities_creatures::creature::is_alive(e, c) && g::models::potion_model::is_valid_target(potion, e, c) {
                        actions.push(CombatAction::Potion {
                            slot,
                            target: Some(uid(e, c)),
                        });
                    }
                }
            } else if target_type == TargetType::AnyAlly {
                for a in g::combat::combat_state::allies(st, c) {
                    if g::models::potion_model::is_valid_target(potion, a, c) {
                        actions.push(CombatAction::Potion {
                            slot,
                            target: Some(uid(a, c)),
                        });
                    }
                }
            } else {
                let me = g::entities_players::player::creature(player, c);
                if g::models::potion_model::is_valid_target(potion, Obj::NULL, c) || g::models::potion_model::is_valid_target(potion, me, c)
                {
                    actions.push(CombatAction::Potion { slot, target: None });
                }
            }
        }
    }

    actions.push(CombatAction::EndTurn);
    actions
}

/// C#: CombatActions.Uid.
fn uid(creature: Creature, c: &mut Combat) -> u32 {
    match g::entities_creatures::creature::combat_id(creature, c) {
        Some(id) => id,
        None => panic!("{creature:?} has no combat id"),
    }
}
