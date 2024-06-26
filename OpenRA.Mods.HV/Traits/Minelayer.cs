#region Copyright & License Information
/*
 * Copyright 2019-2021 The OpenHV Developers (see CREDITS)
 * This file is part of OpenHV, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.HV.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.HV.Traits
{
	public class MinelayerInfo : TraitInfo, Requires<RearmableInfo>
	{
		[FieldLoader.Require]
		[ActorReference(typeof(MineInfo))]
		public readonly string[] Mines = null;

		public readonly string AmmoPoolName = "primary";

		public readonly WDist MinefieldDepth = new WDist(1536);

		[VoiceReference]
		[Desc("Voice to use when ordered to lay a minefield.")]
		public readonly string Voice = "Action";

		[Desc("Sprite image used for overlays.")]
		public readonly string TileImage = "overlay";

		[SequenceReference("TileImage")]
		[Desc("Sprite overlay to use for valid minefield cells.")]
		public readonly string TileValidName = "target-valid";

		[SequenceReference("TileImage")]
		[Desc("Sprite overlay to use for invalid minefield cells.")]
		public readonly string TileInvalidName = "target-invalid";

		[SequenceReference("TileImage")]
		[Desc("Sprite overlay to use for minefield cells hidden behind fog or shroud.")]
		public readonly string TileUnknownName = "target-unknown";

		[CursorReference]
		[Desc("Cursor to display when able to (un)deploy the actor.")]
		public readonly string Cursor = "ability";

		[Desc("Color to use for the target line when laying mines.")]
		public readonly Color TargetLineColor = Color.Crimson;

		[Desc("Only allow laying mines on listed terrain types. Leave empty to allow all terrain types.")]
		public readonly HashSet<string> TerrainTypes = new HashSet<string>();

		[CursorReference]
		[Desc("Cursor to display when able to lay a mine.")]
		public readonly string DeployCursor = "deploy";

		[CursorReference]
		[Desc("Cursor to display when unable to lay a mine.")]
		public readonly string DeployBlockedCursor = "deploy-blocked";

		public override object Create(ActorInitializer init) { return new Minelayer(init.Self, this); }
	}

	public class Minelayer : IIssueOrder, IResolveOrder, ISync, IIssueDeployOrder, IOrderVoice, ITick
	{
		public readonly MinelayerInfo Info;
		public readonly Sprite Tile;

		readonly Actor self;

		[Sync]
		CPos minefieldStart;

		public Minelayer(Actor self, MinelayerInfo info)
		{
			Info = info;
			this.self = self;

			Tile = self.World.Map.Rules.Sequences.GetSequence("overlay", Info.TileValidName).GetSprite(0);
		}

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				yield return new BeginMinefieldOrderTargeter();
				yield return new DeployOrderTargeter("PlaceMine", 5, () => IsCellAcceptable(self, self.Location) ? Info.DeployCursor : Info.DeployBlockedCursor);
			}
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			switch (order.OrderID)
			{
				case "BeginMinefield":
					var start = self.World.Map.CellContaining(target.CenterPosition);
					if (self.World.OrderGenerator is MinefieldOrderGenerator minefieldOrderGenerator)
						minefieldOrderGenerator.AddMinelayer(self);
					else
						self.World.OrderGenerator = new MinefieldOrderGenerator(self, start, queued);

					return new Order("BeginMinefield", self, Target.FromCell(self.World, start), queued);
				case "PlaceMine":
					return new Order("PlaceMine", self, Target.FromCell(self.World, self.Location), queued);
				default:
					return null;
			}
		}

		Order IIssueDeployOrder.IssueDeployOrder(Actor self, bool queued)
		{
			return new Order("PlaceMine", self, Target.FromCell(self.World, self.Location), queued);
		}

		bool IIssueDeployOrder.CanIssueDeployOrder(Actor self, bool queued)
		{
			return IsCellAcceptable(self, self.Location);
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != "BeginMinefield" && order.OrderString != "PlaceMinefield" && order.OrderString != "PlaceMine")
				return;

			var cell = self.World.Map.CellContaining(order.Target.CenterPosition);
			if (order.OrderString == "BeginMinefield")
				minefieldStart = cell;
			else if (order.OrderString == "PlaceMine")
			{
				if (IsCellAcceptable(self, cell))
					self.QueueActivity(order.Queued, new LayMines(self));
			}
			else if (order.OrderString == "PlaceMinefield")
			{
				// A different minelayer might have started laying the field without this minelayer knowing the start
				minefieldStart = order.ExtraLocation;

				var movement = self.Trait<IPositionable>();
				var mobile = movement as Mobile;

				var minefield = GetMinefieldCells(minefieldStart, cell, Info.MinefieldDepth)
					.Where(c => IsCellAcceptable(self, c) && self.Owner.Shroud.IsExplored(c)
						&& movement.CanEnterCell(c, null, BlockedByActor.Immovable) && (mobile != null && mobile.CanStayInCell(c)))
					.OrderBy(c => (c - minefieldStart).LengthSquared).ToList();

				self.QueueActivity(order.Queued, new LayMines(self, minefield));
				self.ShowTargetLines();
			}
		}

		void ITick.Tick(Actor self)
		{
			if (self.CurrentActivity != null)
				foreach (var field in self.CurrentActivity.ActivitiesImplementing<LayMines>())
					field.CleanMineField(self);
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order)
		{
			if (order.OrderString == "PlaceMine" || order.OrderString == "PlaceMinefield")
				return Info.Voice;

			return null;
		}

		static IEnumerable<CPos> GetMinefieldCells(CPos start, CPos end, WDist depth)
		{
			var mins = new CPos(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y));
			var maxs = new CPos(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));

			// TODO: proper endcaps, if anyone cares (which won't happen unless depth is large)
			var p = end - start;
			var q = new float2(p.Y, -p.X);
			q = (start != end) ? (1 / q.Length) * q : new float2(1, 0);
			var c = -float2.Dot(q, new float2(start.X, start.Y));

			// return all points such that |ax + by + c| < depth
			for (var i = mins.X; i <= maxs.X; i++)
				for (var j = mins.Y; j <= maxs.Y; j++)
					if (Math.Abs(q.X * i + q.Y * j + c) * 1024 < depth.Length)
						yield return new CPos(i, j);
		}

		public bool IsCellAcceptable(Actor self, CPos cell)
		{
			if (!self.World.Map.Contains(cell))
				return false;

			if (Info.TerrainTypes.Count == 0)
				return true;

			var terrainType = self.World.Map.GetTerrainInfo(cell).Type;
			return Info.TerrainTypes.Contains(terrainType);
		}

		class MinefieldOrderGenerator : OrderGenerator
		{
			readonly List<Actor> minelayers;
			readonly Minelayer minelayer;
			readonly Sprite validTile, unknownTile, blockedTile;
			readonly float validAlpha, unknownAlpha, blockedAlpha;
			readonly CPos minefieldStart;
			readonly bool queued;

			public MinefieldOrderGenerator(Actor a, CPos xy, bool queued)
			{
				minelayers = new List<Actor>() { a };
				minelayer = a.Trait<Minelayer>();
				minefieldStart = xy;
				this.queued = queued;

				var validSequence = a.World.Map.Rules.Sequences.GetSequence(minelayer.Info.TileImage, minelayer.Info.TileValidName);
				validTile = validSequence.GetSprite(0);
				validAlpha = validSequence.GetAlpha(0);

				var unknownSequence = a.World.Map.Rules.Sequences.GetSequence(minelayer.Info.TileImage, minelayer.Info.TileUnknownName);
				unknownTile = unknownSequence.GetSprite(0);
				unknownAlpha = unknownSequence.GetAlpha(0);

				var blockedSequence = a.World.Map.Rules.Sequences.GetSequence(minelayer.Info.TileImage, minelayer.Info.TileInvalidName);
				blockedTile = blockedSequence.GetSprite(0);
				blockedAlpha = blockedSequence.GetAlpha(0);
			}

			public void AddMinelayer(Actor a)
			{
				minelayers.Add(a);
			}

			protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
			{
				if (mi.Button == Game.Settings.Game.MouseButtonPreference.Cancel)
				{
					world.CancelInputMode();
					yield break;
				}

				if (mi.Button == Game.Settings.Game.MouseButtonPreference.Action)
				{
					minelayers.First().World.CancelInputMode();
					foreach (var minelayer in minelayers)
						yield return new Order("PlaceMinefield", minelayer, Target.FromCell(world, cell), queued) { ExtraLocation = minefieldStart };
				}
			}

			protected override void SelectionChanged(World world, IEnumerable<Actor> selected)
			{
				minelayers.Clear();
				minelayers.AddRange(selected.Where(s => !s.IsDead && s.Info.HasTraitInfo<MinelayerInfo>()));
				if (!minelayers.Any())
					world.CancelInputMode();
			}

			protected override IEnumerable<IRenderable> Render(WorldRenderer wr, World world) { yield break; }
			protected override IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr, World world)
			{
				var minelayer = minelayers.FirstOrDefault(m => m.IsInWorld && !m.IsDead);
				if (minelayer == null)
					yield break;

				// We get the biggest depth so we cover all cells that mines could be placed on.
				var lastMousePos = wr.Viewport.ViewToWorld(Viewport.LastMousePos);
				var minefield = GetMinefieldCells(minefieldStart, lastMousePos,
					minelayers.Max(m => m.Info.TraitInfo<MinelayerInfo>().MinefieldDepth));

				var movement = minelayer.Trait<IPositionable>();
				var mobile = movement as Mobile;
				var pal = wr.Palette(TileSet.TerrainPaletteInternalName);
				foreach (var c in minefield)
				{
					var tile = validTile;
					var alpha = validAlpha;
					if (!world.Map.Contains(c))
					{
						tile = blockedTile;
						alpha = blockedAlpha;
					}
					else if (world.ShroudObscures(c))
					{
						tile = blockedTile;
						alpha = blockedAlpha;
					}
					else if (world.FogObscures(c))
					{
						tile = unknownTile;
						alpha = unknownAlpha;
					}
					else if (!this.minelayer.IsCellAcceptable(minelayer, c)
						|| !movement.CanEnterCell(c, null, BlockedByActor.Immovable) || (mobile != null && !mobile.CanStayInCell(c)))
					{
						tile = blockedTile;
						alpha = blockedAlpha;
					}

					yield return new SpriteRenderable(tile, world.Map.CenterOfCell(c), WVec.Zero, -511, pal, 1f, alpha, float3.Ones, TintModifiers.IgnoreWorldTint, true);
				}
			}

			protected override IEnumerable<IRenderable> RenderAnnotations(WorldRenderer wr, World world) { yield break; }

			protected override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
			{
				return minelayers.First().Trait<Minelayer>().Info.Cursor;
			}
		}

		class BeginMinefieldOrderTargeter : IOrderTargeter
		{
			public string OrderID { get { return "BeginMinefield"; } }
			public int OrderPriority { get { return 5; } }
			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) { return true; }

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (target.Type != TargetType.Terrain)
					return false;

				var location = self.World.Map.CellContaining(target.CenterPosition);
				if (!self.World.Map.Contains(location))
					return false;

				var minelayer = self.Trait<Minelayer>();
				cursor = minelayer.Info.Cursor;
				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);

				return modifiers.HasModifier(TargetModifiers.ForceAttack);
			}

			public bool IsQueued { get; protected set; }
		}
	}
}
