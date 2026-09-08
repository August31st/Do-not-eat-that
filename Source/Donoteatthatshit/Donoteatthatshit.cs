using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Donoteatthatshit;

[HarmonyPatch(typeof(JobDriver_Ingest), nameof(JobDriver_Ingest.Notify_Starting))]
internal static class IngestStartingPatch
{
    public static void Prefix(JobDriver_Ingest __instance)
    {
        MealPoisoningTracker.PrepareBowlSmashDecision(__instance.pawn);
    }
}

public sealed class DonoteatthatshitMod : Mod
{
    public static DonoteatthatshitSettings Settings { get; private set; }

    public DonoteatthatshitMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<DonoteatthatshitSettings>();
        new Harmony("local.donoteatthatshit").PatchAll();
    }

    public override string SettingsCategory()
    {
        return Content.Name;
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        Listing_Standard listing = new Listing_Standard();
        listing.Begin(inRect);
        int oldCookingThreshold = Settings.CookingThreshold;
        int oldIntellectualThreshold = Settings.IntellectualThreshold;
        bool oldLockBowlSmashToNormalSpeed = Settings.LockBowlSmashToNormalSpeed;
        bool oldPreserveNutritionWhenFoodIsLow = Settings.PreserveNutritionWhenFoodIsLow;
        listing.TextFieldNumericLabeled("Donoteatthatshit_IntellectualThreshold".Translate(), ref Settings.IntellectualThreshold, ref Settings.IntellectualBuffer, 0, 20);
        listing.TextFieldNumericLabeled("Donoteatthatshit_CookingThreshold".Translate(), ref Settings.CookingThreshold, ref Settings.CookingBuffer, 0, 20);
        listing.CheckboxLabeled("Donoteatthatshit_LockBowlSmashToNormalSpeed".Translate(), ref Settings.LockBowlSmashToNormalSpeed);
        listing.CheckboxLabeled("Donoteatthatshit_PreserveNutritionWhenFoodIsLow".Translate(), ref Settings.PreserveNutritionWhenFoodIsLow);
        if (listing.ButtonText("Donoteatthatshit_ResetSettings".Translate()))
        {
            Settings.CookingThreshold = 8;
            Settings.IntellectualThreshold = 6;
            Settings.LockBowlSmashToNormalSpeed = true;
            Settings.PreserveNutritionWhenFoodIsLow = false;
            Settings.CookingBuffer = "8";
            Settings.IntellectualBuffer = "6";
            Settings.Write();
        }
        listing.End();
        if (oldCookingThreshold != Settings.CookingThreshold || oldIntellectualThreshold != Settings.IntellectualThreshold || oldLockBowlSmashToNormalSpeed != Settings.LockBowlSmashToNormalSpeed || oldPreserveNutritionWhenFoodIsLow != Settings.PreserveNutritionWhenFoodIsLow)
        {
            Settings.Write();
        }
    }
}

public sealed class DonoteatthatshitSettings : ModSettings
{
    public int CookingThreshold = 8;
    public int IntellectualThreshold = 6;
    public bool LockBowlSmashToNormalSpeed = true;
    public bool PreserveNutritionWhenFoodIsLow;
    public string CookingBuffer = "8";
    public string IntellectualBuffer = "6";

    public override void ExposeData()
    {
        Scribe_Values.Look(ref CookingThreshold, "cookingThreshold", 8);
        Scribe_Values.Look(ref IntellectualThreshold, "intellectualThreshold", 6);
        Scribe_Values.Look(ref LockBowlSmashToNormalSpeed, "lockBowlSmashToNormalSpeed", true);
        Scribe_Values.Look(ref PreserveNutritionWhenFoodIsLow, "preserveNutritionWhenFoodIsLow", false);
    }
}

[HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.AddFoodPoisoningHediff))]
internal static class AddFoodPoisoningHediffPatch
{
    public static void Prefix(Pawn pawn)
    {
        MealPoisoningTracker.NotifyPoisoningAttempt(pawn);
    }
}

[HarmonyPatch(typeof(Messages), nameof(Messages.Message), new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) })]
internal static class FoodPoisoningMessagePatch
{
    public static bool Prefix()
    {
        return !MealPoisoningTracker.ConsumeFoodPoisoningMessageSuppression();
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.Ingested))]
internal static class ThingIngestedPatch
{
    public static void Prefix(Pawn ingester)
    {
        MealPoisoningTracker.BeginIngestion(ingester);
    }

    public static void Postfix(Pawn ingester, ref float __result)
    {
        if (!MealPoisoningTracker.EndIngestion(ingester))
        {
            return;
        }

        TraitMealOutcome traitOutcome = TryGetTraitOutcome(ingester);
        if (traitOutcome != null)
        {
            ApplyTraitOutcome(ingester, traitOutcome, ref __result);
            if (!traitOutcome.vomit && MealPoisoningTracker.ConsumeBowlSmashDecision(ingester))
            {
                StartBowlSmash(ingester);
            }
            return;
        }

        if (DonoteatthatshitMod.Settings.PreserveNutritionWhenFoodIsLow && MealPoisoningTracker.IsLowFoodAlertActive(ingester))
        {
            ApplyLowFoodOutcome(ingester);
            return;
        }

        __result = 0f;
        Hediff foodPoisoning = ingester.health?.hediffSet.GetFirstHediffOfDef(HediffDefOf.FoodPoisoning);
        if (!MealPoisoningTracker.HadFoodPoisoningBeforeMeal(ingester) && foodPoisoning != null)
        {
            ingester.health.RemoveHediff(foodPoisoning);
        }

        ThoughtDef thoughtDef = DefDatabase<ThoughtDef>.GetNamedSilentFail("Donoteatthatshit_SuspiciousMeal");
        if (!HasKindTrait(ingester) && thoughtDef != null && ingester.needs?.mood != null)
        {
            Thought_Memory memory = ingester.needs.mood.thoughts.memories.GetFirstMemoryOfDef(thoughtDef);
            if (memory == null)
            {
                ingester.needs.mood.thoughts.memories.TryGainMemory(thoughtDef);
            }
            else
            {
                memory.SetForcedStage(Mathf.Min(memory.CurStageIndex + 1, 2));
                memory.Renew();
            }
        }

        if (PawnUtility.ShouldSendNotificationAbout(ingester) && MessagesRepeatAvoider.MessageShowAllowed("Donoteatthatshit-FoodSuspicion-" + ingester.thingIDNumber, 0.1f))
        {
            Messages.Message("Donoteatthatshit_FoodSuspicionMessage".Translate(ingester.Named("PAWN")), ingester, MessageTypeDefOf.NegativeEvent);
        }

        if (!ingester.Dead && ingester.jobs != null)
        {
            MealPoisoningTracker.QueueBowlSmashEvent(ingester);
            ingester.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Vomit), JobCondition.InterruptForced, null, resumeCurJobAfterwards: true);
        }
    }

    private static void ApplyLowFoodOutcome(Pawn pawn)
    {
        bool hadFoodPoisoningBeforeMeal = MealPoisoningTracker.HadFoodPoisoningBeforeMeal(pawn);
        if (!hadFoodPoisoningBeforeMeal)
        {
            Hediff foodPoisoning = pawn.health?.hediffSet.GetFirstHediffOfDef(HediffDefOf.FoodPoisoning);
            if (foodPoisoning != null)
            {
                pawn.health.RemoveHediff(foodPoisoning);
            }
        }

        if (PawnUtility.ShouldSendNotificationAbout(pawn) && MessagesRepeatAvoider.MessageShowAllowed("Donoteatthatshit-FoodLowNutrition-" + pawn.thingIDNumber, 0.1f))
        {
            Messages.Message("Donoteatthatshit_FoodLowNutritionMessage".Translate(pawn.Named("PAWN_label")), pawn, MessageTypeDefOf.NegativeEvent);
        }

        if (!pawn.Dead && pawn.jobs != null)
        {
            MealPoisoningTracker.QueueBowlSmashEvent(pawn);
            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Vomit), JobCondition.InterruptForced, null, resumeCurJobAfterwards: true);
        }
    }

    internal static void StartBowlSmash(Pawn pawn)
    {
        MentalStateDef bowlSmashDef = DefDatabase<MentalStateDef>.GetNamedSilentFail("Donoteatthatshit_BowlSmash");
        if (bowlSmashDef != null)
        {
            pawn.mindState?.mentalStateHandler.TryStartMentalState(bowlSmashDef, forced: true, forceWake: true, transitionSilently: true);
        }
    }

    private static TraitMealOutcome TryGetTraitOutcome(Pawn pawn)
    {
        List<Func<Pawn, TraitMealOutcome>> outcomeResolvers = new List<Func<Pawn, TraitMealOutcome>>
        {
            GetKindOutcome,
            GetMasochistOutcome,
            GetAsceticOutcome,
            GetSicklyOutcome,
            GetGourmandOutcome
        };

        foreach (Func<Pawn, TraitMealOutcome> resolver in outcomeResolvers)
        {
            TraitMealOutcome outcome = resolver(pawn);
            if (outcome != null)
            {
                return outcome;
            }
        }

        return null;
    }

    private static TraitMealOutcome GetKindOutcome(Pawn pawn)
    {
        return pawn.story?.traits?.HasTrait(TraitDefOf.Kind) == true
            ? new TraitMealOutcome(null, "Donoteatthatshit_KindFoodSuspicionMessage".Translate(pawn.Named("PAWN_label")), MessageTypeDefOf.NegativeEvent, nutritionZero: true, vomit: true)
            : null;
    }

    private static TraitMealOutcome GetAsceticOutcome(Pawn pawn)
    {
        return pawn.story?.traits?.HasTrait(TraitDefOf.Ascetic) == true
            ? new TraitMealOutcome("Donoteatthatshit_AsceticRottenMeal", "Donoteatthatshit_AsceticFoodSuspicionMessage".Translate(pawn.Named("PAWN_label")), MessageTypeDefOf.NegativeEvent, retainFoodPoisoning: true, preserveNutrition: true)
            : null;
    }

    private static TraitMealOutcome GetMasochistOutcome(Pawn pawn)
    {
        TraitDef masochistTrait = DefDatabase<TraitDef>.GetNamedSilentFail("Masochist");
        return masochistTrait != null && pawn.story?.traits?.HasTrait(masochistTrait) == true
            ? new TraitMealOutcome("Donoteatthatshit_MasochistRottenMeal", "Donoteatthatshit_MasochistFoodSuspicionMessage".Translate(pawn.Named("PAWN_label")), MessageTypeDefOf.PositiveEvent, retainFoodPoisoning: true, preserveNutrition: true)
            : null;
    }

    private static TraitMealOutcome GetSicklyOutcome(Pawn pawn)
    {
        TraitDef sicklyTrait = DefDatabase<TraitDef>.GetNamedSilentFail("Sickly");
        if (sicklyTrait == null || pawn.story?.traits?.HasTrait(sicklyTrait) != true)
        {
            return null;
        }

        PawnCapacityDef metabolismDef = DefDatabase<PawnCapacityDef>.GetNamedSilentFail("Metabolism");
        float metabolism = metabolismDef == null ? 0f : pawn.health?.capacities?.GetLevel(metabolismDef) ?? 0f;
        float successChance = Mathf.Clamp01(0.5f + (metabolism - 1f));
        if (Rand.Chance(successChance))
        {
            return CreateStandardOutcome();
        }

        return new TraitMealOutcome(
            null,
            "Donoteatthatshit_SicklyFoodSuspicionMessage".Translate(pawn.Named("PAWN_label")),
            MessageTypeDefOf.NegativeEvent,
            nutritionZero: true,
            vomit: true,
            retainFoodPoisoning: true,
            useSuspiciousThought: true);
    }

    private static TraitMealOutcome GetGourmandOutcome(Pawn pawn)
    {
        TraitDef gourmandTrait = DefDatabase<TraitDef>.GetNamedSilentFail("Gourmand");
        if (gourmandTrait == null || pawn.story?.traits?.HasTrait(gourmandTrait) != true)
        {
            return null;
        }

        float eatingSpeed = pawn.GetStatValue(StatDefOf.EatingSpeed);
        float successChance = Mathf.Clamp01(1f - Mathf.Max(0f, eatingSpeed - 1f) * 2f);
        if (Rand.Chance(successChance))
        {
            return CreateStandardOutcome();
        }

        return new TraitMealOutcome(
            null,
            "Donoteatthatshit_GourmandFoodSuspicionMessage".Translate(pawn.Named("PAWN_label")),
            MessageTypeDefOf.NegativeEvent,
            retainFoodPoisoning: true,
            preserveNutrition: true);
    }

    private static TraitMealOutcome CreateStandardOutcome()
    {
        return new TraitMealOutcome(
            null,
            "Donoteatthatshit_FoodSuspicionMessage".Translate(),
            MessageTypeDefOf.NegativeEvent,
            nutritionZero: true,
            vomit: true,
            useSuspiciousThought: true);
    }

    private static void ApplyTraitOutcome(Pawn pawn, TraitMealOutcome outcome, ref float nutritionResult)
    {
        if (!outcome.preserveNutrition)
        {
            nutritionResult = 0f;
        }

        bool hadFoodPoisoningBeforeMeal = MealPoisoningTracker.HadFoodPoisoningBeforeMeal(pawn);
        if (!outcome.retainFoodPoisoning && !hadFoodPoisoningBeforeMeal)
        {
            Hediff foodPoisoning = pawn.health?.hediffSet.GetFirstHediffOfDef(HediffDefOf.FoodPoisoning);
            if (foodPoisoning != null)
            {
                pawn.health.RemoveHediff(foodPoisoning);
            }
        }

        if (outcome.useSuspiciousThought)
        {
            AddSuspiciousThought(pawn);
        }
        else if (outcome.thoughtDefName != null)
        {
            ThoughtDef thoughtDef = DefDatabase<ThoughtDef>.GetNamedSilentFail(outcome.thoughtDefName);
            if (thoughtDef != null && pawn.needs?.mood != null)
            {
                pawn.needs.mood.thoughts.memories.TryGainMemory(thoughtDef);
            }
        }

        if (PawnUtility.ShouldSendNotificationAbout(pawn) && MessagesRepeatAvoider.MessageShowAllowed("Donoteatthatshit-FoodSuspicion-" + pawn.thingIDNumber, 0.1f))
        {
            Messages.Message(outcome.message.CapitalizeFirst(), pawn, outcome.messageType);
        }

        if (outcome.vomit && !pawn.Dead && pawn.jobs != null)
        {
            MealPoisoningTracker.QueueBowlSmashEvent(pawn);
            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Vomit), JobCondition.InterruptForced, null, resumeCurJobAfterwards: true);
        }
    }

    private static void AddSuspiciousThought(Pawn pawn)
    {
        ThoughtDef thoughtDef = DefDatabase<ThoughtDef>.GetNamedSilentFail("Donoteatthatshit_SuspiciousMeal");
        if (thoughtDef != null && pawn.needs?.mood != null)
        {
            Thought_Memory memory = pawn.needs.mood.thoughts.memories.GetFirstMemoryOfDef(thoughtDef);
            if (memory == null)
            {
                pawn.needs.mood.thoughts.memories.TryGainMemory(thoughtDef);
            }
            else
            {
                memory.SetForcedStage(Mathf.Min(memory.CurStageIndex + 1, 2));
                memory.Renew();
            }
        }
    }

    private sealed class TraitMealOutcome
    {
        public readonly string thoughtDefName;
        public readonly string message;
        public readonly MessageTypeDef messageType;
        public readonly bool nutritionZero;
        public readonly bool vomit;
        public readonly bool retainFoodPoisoning;
        public readonly bool preserveNutrition;
        public readonly bool useSuspiciousThought;

        public TraitMealOutcome(
            string thoughtDefName,
            string message,
            MessageTypeDef messageType,
            bool nutritionZero = false,
            bool vomit = false,
            bool retainFoodPoisoning = false,
            bool preserveNutrition = false,
            bool useSuspiciousThought = false)
        {
            this.thoughtDefName = thoughtDefName;
            this.message = message;
            this.messageType = messageType;
            this.nutritionZero = nutritionZero;
            this.vomit = vomit;
            this.retainFoodPoisoning = retainFoodPoisoning;
            this.preserveNutrition = preserveNutrition;
            this.useSuspiciousThought = useSuspiciousThought;
        }
    }

    internal static bool ShouldTriggerEvent(Pawn pawn)
    {
        DonoteatthatshitSettings settings = DonoteatthatshitMod.Settings;
        int cookingLevel = pawn.skills?.GetSkill(SkillDefOf.Cooking).Level ?? 0;
        int intellectualLevel = pawn.skills?.GetSkill(SkillDefOf.Intellectual).Level ?? 0;
        if (cookingLevel > settings.CookingThreshold || intellectualLevel > settings.IntellectualThreshold)
        {
            return true;
        }

        float cookingChance = RatioToThreshold(cookingLevel, settings.CookingThreshold);
        float intellectualChance = RatioToThreshold(intellectualLevel, settings.IntellectualThreshold);
        return Rand.Chance(Mathf.Max(cookingChance, intellectualChance));
    }

    internal static float GetBowlSmashChance(Pawn pawn)
    {
        if (HasKindTrait(pawn))
        {
            return 0f;
        }

        TraitDef bloodlustTrait = DefDatabase<TraitDef>.GetNamedSilentFail("Bloodlust");
        if (bloodlustTrait != null && pawn.story?.traits?.HasTrait(bloodlustTrait) == true)
        {
            return 1f;
        }

        MentalBreaker mentalBreaker = pawn.mindState?.mentalBreaker;
        if (mentalBreaker == null || mentalBreaker.CurMood > mentalBreaker.BreakThresholdMinor)
        {
            return 0f;
        }

        if (mentalBreaker.CurMood <= mentalBreaker.BreakThresholdExtreme)
        {
            return 1f;
        }

        if (mentalBreaker.CurMood <= mentalBreaker.BreakThresholdMajor)
        {
            return 0.7f;
        }

        return 0.2f;
    }

    private static bool HasKindTrait(Pawn pawn)
    {
        return pawn.story?.traits?.HasTrait(TraitDefOf.Kind) == true;
    }

    private static float RatioToThreshold(int level, int threshold)
    {
        if (threshold <= 0)
        {
            return level > 0 ? 1f : 0f;
        }

        return Mathf.Clamp01((float)level / threshold);
    }

}

[HarmonyPatch(typeof(JobDriver), nameof(JobDriver.EndJobWith))]
internal static class VomitCleanupPatch
{
    public static void Postfix(JobDriver __instance, JobCondition condition)
    {
        if (__instance is JobDriver_Vomit)
        {
            MealPoisoningTracker.CompleteQueuedBowlSmashEvent(__instance.pawn, condition == JobCondition.Succeeded);
        }
    }
}

public sealed class MentalState_BowlSmash : MentalState
{
    private TimeSpeed previousTimeSpeed;
    private bool speedWasLocked;
    private bool attackStarted;

    public override void PostStart(string reason)
    {
        base.PostStart(reason);

        speedWasLocked = DonoteatthatshitMod.Settings.LockBowlSmashToNormalSpeed;
        if (speedWasLocked)
        {
            previousTimeSpeed = Find.TickManager.CurTimeSpeed;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
        }

        SoundDef soundDef = DefDatabase<SoundDef>.GetNamedSilentFail("Donoteatthatshit_BowlSmash");
        soundDef?.PlayOneShot(SoundInfo.InMap(pawn));

        Thing table = FindTableAtEatingSurface();
        float smokeRadius = table == null ? 2f : 1f;
        if (table != null)
        {
            attackStarted = TryStartMeleeAttack(table);
        }

        if (pawn.Spawned)
        {
            GenExplosion.DoExplosion(
                pawn.Position,
                pawn.Map,
                smokeRadius,
                DamageDefOf.Smoke,
                pawn,
                postExplosionGasType: GasType.BlindSmoke,
                postExplosionGasRadiusOverride: smokeRadius);
        }
    }

    public override void MentalStateTick(int delta)
    {
        if (speedWasLocked && Find.TickManager.CurTimeSpeed != TimeSpeed.Normal)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
        }

        if (!attackStarted)
        {
            Thing table = FindTableAtEatingSurface();
            if (table != null)
            {
                attackStarted = TryStartMeleeAttack(table);
            }
        }

        base.MentalStateTick(delta);
    }

    public override void PostEnd()
    {
        base.PostEnd();
        if (speedWasLocked)
        {
            Find.TickManager.CurTimeSpeed = previousTimeSpeed;
        }
    }

    private Thing FindTableAtEatingSurface()
    {
        if (pawn.CurJob == null || pawn.CurJob.def != JobDefOf.Ingest)
        {
            return null;
        }

        IntVec3 eatingSurface = pawn.CurJob.GetTarget(TargetIndex.B).Cell;
        if (!eatingSurface.InBounds(pawn.Map))
        {
            return null;
        }

        foreach (Thing thing in eatingSurface.GetThingList(pawn.Map))
        {
            if (thing.def.IsTable)
            {
                return thing;
            }
        }

        return null;
    }

    private bool TryStartMeleeAttack(Thing table)
    {
        Verb verb = pawn.TryGetAttackVerb(table, false, false);
        if (verb == null || !verb.IsMeleeAttack || !verb.CanHitTarget(table))
        {
            return false;
        }

        return verb.TryStartCastOn(table, false, false, false, false);
    }
}

internal static class MealPoisoningTracker
{
    private static readonly Dictionary<Pawn, bool> ActiveMeals = new();
    private static readonly HashSet<Pawn> PoisonedMeals = new();
    private static readonly HashSet<Pawn> SpecialMeals = new();
    private static readonly HashSet<Pawn> QueuedBowlSmashEvents = new();
    private static readonly Dictionary<Pawn, bool> BowlSmashDecisions = new();
    private static readonly HashSet<Pawn> SuppressFoodPoisoningMessages = new();
    private static readonly Dictionary<Pawn, bool> PreviousFoodPoisoning = new();

    public static void BeginIngestion(Pawn pawn)
    {
        if (pawn == null)
        {
            return;
        }

        ActiveMeals[pawn] = true;
        PoisonedMeals.Remove(pawn);
        SpecialMeals.Remove(pawn);
        QueuedBowlSmashEvents.Remove(pawn);
        SuppressFoodPoisoningMessages.Remove(pawn);
        PreviousFoodPoisoning[pawn] = pawn.health?.hediffSet.GetFirstHediffOfDef(HediffDefOf.FoodPoisoning) != null;
    }

    public static void NotifyPoisoningAttempt(Pawn pawn)
    {
        if (pawn != null && ActiveMeals.ContainsKey(pawn))
        {
            PoisonedMeals.Add(pawn);
            if (ThingIngestedPatch.ShouldTriggerEvent(pawn))
            {
                SpecialMeals.Add(pawn);
                SuppressFoodPoisoningMessages.Add(pawn);
            }
        }
    }

    public static bool EndIngestion(Pawn pawn)
    {
        if (pawn == null || !ActiveMeals.Remove(pawn))
        {
            return false;
        }

        bool poisoned = PoisonedMeals.Remove(pawn);
        bool special = SpecialMeals.Remove(pawn);
        SuppressFoodPoisoningMessages.Remove(pawn);
        if (!special)
        {
            BowlSmashDecisions.Remove(pawn);
        }
        return poisoned && special;
    }

    public static bool ConsumeFoodPoisoningMessageSuppression()
    {
        Pawn pawn = null;
        foreach (Pawn activePawn in SuppressFoodPoisoningMessages)
        {
            pawn = activePawn;
            break;
        }

        return pawn != null && SuppressFoodPoisoningMessages.Remove(pawn);
    }

    public static void PrepareBowlSmashDecision(Pawn pawn)
    {
        if (pawn != null)
        {
            float bowlSmashChance = ThingIngestedPatch.GetBowlSmashChance(pawn);
            BowlSmashDecisions[pawn] = Rand.Chance(bowlSmashChance);
        }
    }

    public static bool ConsumeBowlSmashDecision(Pawn pawn)
    {
        if (pawn == null || !BowlSmashDecisions.TryGetValue(pawn, out bool shouldTrigger))
        {
            return false;
        }

        BowlSmashDecisions.Remove(pawn);
        return shouldTrigger;
    }

    public static void QueueBowlSmashEvent(Pawn pawn)
    {
        if (ConsumeBowlSmashDecision(pawn))
        {
            QueuedBowlSmashEvents.Add(pawn);
        }
    }

    public static void CompleteQueuedBowlSmashEvent(Pawn pawn, bool succeeded)
    {
        if (pawn != null && QueuedBowlSmashEvents.Remove(pawn) && succeeded)
        {
            ThingIngestedPatch.StartBowlSmash(pawn);
        }
    }

    public static bool HadFoodPoisoningBeforeMeal(Pawn pawn)
    {
        bool previous = PreviousFoodPoisoning.TryGetValue(pawn, out bool value) && value;
        PreviousFoodPoisoning.Remove(pawn);
        return previous;
    }

    public static bool IsLowFoodAlertActive(Pawn pawn)
    {
        Map map = pawn?.Map;
        if (map == null || !map.IsPlayerHome || !map.mapPawns.AnyColonistSpawned || Find.TickManager.TicksGame < 150000)
        {
            return false;
        }

        return map.resourceCounter.TotalHumanEdibleNutrition < 4f * map.mapPawns.FreeColonistsSpawnedCount;
    }
}