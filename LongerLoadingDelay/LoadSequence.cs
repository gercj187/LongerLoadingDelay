//FILE:		LoadSequence.cs

using System;
using System.Collections.Generic;
using DV.Logic.Job;
using DV.ThingTypes;

namespace LongerLoadingDelay
{
	// =========================
	// ZUSTÄNDE
	// =========================
	public enum SeqState
	{
		Queued,			// wartet auf freie Maschine / stehende Wagen
		Active,			// wird gerade be-/entladen
		Interrupted,	// Wagen bewegt oder nicht vollständig auf dem Gleis
		Done			// alle Wagen fertig, Wagen stehen noch am Gleis
	}

	public enum InterruptReason
	{
		None,
		Moving,
		CarsMissing
	}

	public enum LampState
	{
		None,		// Mod aus / kein Zustand → Originalfarbe
		Idle,		// weiß: Gleis bereit
		Loading,	// gelb blinkend
		Paused,		// blau dauerhaft (Pause laut Schichtplan)
		Error,		// rot schnell blinkend
		Finished	// grün
	}

	// =========================
	// SEQUENZ (eine pro Job + Gleis + Richtung)
	// =========================
	public class LoadSequence
	{
		// --- gespeichert ---
		public string JobId = "";
		public string TrackName = "";
		public bool IsLoading = true;
		public int JobCars;
		public int CarsDone;
		public double WorkDone;			// "Arbeitssekunden" (Spielsekunden bei Faktor 1.0)
		public double DelaySeconds;		// Arbeitssekunden pro Wagen
		public SeqState State = SeqState.Queued;
		public double FinishedAt = -1;		// absolute Spielminuten, wann fertig (für Anzeige und Standgeld)

		// --- nur zur Laufzeit ---
		[NonSerialized] public Job? Job;
		[NonSerialized] public WarehouseMachine? Machine;
		[NonSerialized] public List<Car>? Cars;
		[NonSerialized] public InterruptReason Reason;
		[NonSerialized] public float StableFor;
		[NonSerialized] public float MissingJobFor;
		[NonSerialized] public double FinishHint = -1;	// exakter Fertig-Zeitpunkt aus dem letzten Arbeits-Tick
		[NonSerialized] public float GoneFor;			// Sekunden ohne Wagen am Gleis (→ abmelden)

		public double TotalWork => JobCars * DelaySeconds;
		public double RemainingWork => Math.Max(0.0, TotalWork - WorkDone);

		public WarehouseTaskType TaskType =>
			IsLoading ? WarehouseTaskType.Loading : WarehouseTaskType.Unloading;

		public bool IsOperating => State != SeqState.Done;
	}
}
