//! What an export cannot carry: delegates. A monster's state machine is made of them (what a move does, when a branch
//! is taken, how much an intent shows), and a calculated var of a card holds one. An imported combat has those objects
//! with their plain fields and no delegate, so each is given its delegate again from where the game makes it: the
//! monster's own `GenerateMoveStateMachine`, the model's own `CanonicalVars`. The code runs on a copy of the combat
//! (it may draw from a random stream); only the delegates are taken from it, state by state and var by var, by id.

use crate::r#gen as g;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::rt::*;

type Res = Result<(), String>;

/// Gives every object of an imported combat that holds a delegate its delegate.
pub fn attach(c: &mut Combat) -> Res {
    monsters(c)?;
    vars(c)
}

fn monsters(c: &mut Combat) -> Res {
    for i in 0..c.a.monster_model.items.len() {
        let monster = Obj::new(MonsterModelData::TAG, i, false);
        let machine = c.a.monster_model[monster]._move_state_machine;
        if machine.is_null() {
            continue;
        }
        let mut scratch = c.clone();
        let fresh = g::dispatch::monster_model::generate_move_state_machine(monster, &mut scratch);
        let what = format!("{:?} of {:?}", machine, g::kinds::kind_of(monster, c));
        let states: Vec<(Str, Obj)> = c.a.monster_move_state_machine[machine].states.clone().into_vec().into_iter().map(|p| (p.key, p.value)).collect();
        let mut known = Vec::new();
        for (id, state) in states {
            let Some(made) = scratch.a.monster_move_state_machine[fresh].states.try_get(&id) else {
                return Err(format!("{what}: the state {} is not one the monster makes", id.as_str()));
            };
            if g::kinds::kind_of(made, &scratch) != g::kinds::kind_of(state, c) {
                return Err(format!("{what}: the state {} is of another class than the one the monster makes", id.as_str()));
            }
            state_delegates(state, made, c, &scratch).map_err(|e| format!("{what}: the state {}: {e}", id.as_str()))?;
            known.push(state);
        }
        // The next move of a monster that has not rolled one yet is the move of no machine: `new MoveState()`. Any
        // other move outside the machine (one made on the spot) has a delegate nobody can make again.
        let next = c.a.monster_model[monster].next_move;
        if !next.is_null() && !known.contains(&next) {
            let unset = g::monster_moves_monster_move_state_machine::move_state::new__0(&mut scratch);
            if MoveStateData::of(next, c).state_id != MoveStateData::of(unset, &scratch).state_id {
                return Err(format!("{what}: its next move is not a state of its machine"));
            }
            state_delegates(next, unset, c, &scratch).map_err(|e| format!("{what}: its next move: {e}"))?;
        }
    }
    Ok(())
}

fn state_delegates(state: Obj, made: Obj, c: &mut Combat, scratch: &Combat) -> Res {
    match (&mut c.a.monster_state[state].ext, &scratch.a.monster_state[made].ext) {
        (MonsterStateExt::MoveState(d), MonsterStateExt::MoveState(m)) => {
            d._on_perform = m._on_perform.clone();
            let (intents, made_intents) = (d.intents.clone(), m.intents.clone());
            if intents.len() != made_intents.len() {
                return Err(format!("{} intents where the monster makes {}", intents.len(), made_intents.len()));
            }
            for (intent, made_intent) in intents.into_iter().zip(made_intents) {
                intent_delegates(intent, made_intent, c, scratch)?;
            }
        }
        (MonsterStateExt::ConditionalBranchState(d), MonsterStateExt::ConditionalBranchState(m)) => {
            if d.states.len() != m.states.len() || d.states.iter().zip(&m.states).any(|(a, b)| a.id != b.id) {
                return Err("its branches are not the ones the monster makes".to_string());
            }
            for (branch, made_branch) in d.states.iter_mut().zip(&m.states) {
                branch._conditional_lambda = made_branch._conditional_lambda.clone();
            }
        }
        (MonsterStateExt::RandomBranchState(d), MonsterStateExt::RandomBranchState(m)) => {
            if d.states.len() != m.states.len() || d.states.iter().zip(&m.states).any(|(a, b)| a.state_id != b.state_id) {
                return Err("its branches are not the ones the monster makes".to_string());
            }
            for (branch, made_branch) in d.states.iter_mut().zip(&m.states) {
                branch.weight_lambda = made_branch.weight_lambda.clone();
            }
        }
        (MonsterStateExt::None, MonsterStateExt::None) => {}
        _ => return Err("of another class than the one the monster makes".to_string()),
    }
    Ok(())
}

fn intent_delegates(intent: Obj, made: Obj, c: &mut Combat, scratch: &Combat) -> Res {
    let kind = g::kinds::kind_of(intent, c);
    if kind != g::kinds::kind_of(made, scratch) {
        return Err(format!("an intent of {kind:?} where the monster makes another"));
    }
    if is_subclass(kind, Kind::AttackIntent) {
        AttackIntentData::of_mut(intent, c).damage_calc = AttackIntentData::of(made, scratch).damage_calc.clone();
    }
    if is_subclass(kind, Kind::MultiAttackIntent) {
        MultiAttackIntentData::of_mut(intent, c)._repeat_calc = MultiAttackIntentData::of(made, scratch)._repeat_calc.clone();
    }
    Ok(())
}

/// The calculated vars: each model's own `CanonicalVars` makes the var with its delegate (a static one: the game
/// refuses any other), and the var of the same name gets it.
fn vars(c: &mut Combat) -> Res {
    let mut models: Vec<(Obj, DynamicVarSet, fn(Obj, &mut Combat) -> Vec<DynamicVar>)> = Vec::new();
    for i in 0..c.a.card_model.items.len() {
        let o = Obj::new(CardModelData::TAG, i, false);
        models.push((o, c.a.card_model[o]._dynamic_vars, g::dispatch::card_model::canonical_vars));
    }
    for i in 0..c.a.relic_model.items.len() {
        let o = Obj::new(RelicModelData::TAG, i, false);
        models.push((o, c.a.relic_model[o]._dynamic_vars, g::dispatch::relic_model::canonical_vars));
    }
    for i in 0..c.a.power_model.items.len() {
        let o = Obj::new(PowerModelData::TAG, i, false);
        models.push((o, c.a.power_model[o]._dynamic_vars, g::dispatch::power_model::canonical_vars));
    }
    for i in 0..c.a.potion_model.items.len() {
        let o = Obj::new(PotionModelData::TAG, i, false);
        models.push((o, c.a.potion_model[o]._dynamic_vars, g::dispatch::potion_model::canonical_vars));
    }
    for i in 0..c.a.enchantment_model.items.len() {
        let o = Obj::new(EnchantmentModelData::TAG, i, false);
        models.push((o, c.a.enchantment_model[o]._dynamic_vars, g::dispatch::enchantment_model::canonical_vars));
    }
    for (model, set, canonical_vars) in models {
        if set.is_null() {
            continue;
        }
        let calculated: Vec<(Str, Obj)> = c.a.dynamic_var_set[set]._vars.clone().into_vec().into_iter().map(|p| (p.key, p.value))
            .filter(|(_, v)| is_subclass(g::kinds::kind_of(*v, c), Kind::CalculatedVar))
            .collect();
        if calculated.is_empty() {
            continue;
        }
        let mut scratch = c.clone();
        let made = canonical_vars(model, &mut scratch);
        for (name, var) in calculated {
            let Some(source) = made.iter().copied().find(|v| scratch.a.dynamic_var[*v].name == name) else {
                return Err(format!("{:?}: the var {} is not one the model makes", g::kinds::kind_of(model, c), name.as_str()));
            };
            if !is_subclass(g::kinds::kind_of(source, &scratch), Kind::CalculatedVar) {
                return Err(format!("{:?}: the var {} is calculated, the one the model makes is not", g::kinds::kind_of(model, c), name.as_str()));
            }
            CalculatedVarData::of_mut(var, c)._multiplier_calc = CalculatedVarData::of(source, &scratch)._multiplier_calc.clone();
        }
    }
    Ok(())
}
