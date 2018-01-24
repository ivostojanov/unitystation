using System;
using Tilemaps;
using Tilemaps.Behaviours.Objects;
using Tilemaps.Scripts;
using UnityEngine;
using UnityEngine.Networking;
using Random = UnityEngine.Random;

// ReSharper disable CompareOfFloatsByEqualityOperator
public struct TransformState
{
	public bool Active;
	public float Speed;
	public Vector2 Impulse; //Direction of throw
	public Vector3 localPos;

	public Vector3 position
	{
		get
		{
			if (localPos == CustomNetTransform.InvalidPos)
			{
				return localPos;
			}
			return CustomNetTransform.localToWorld(localPos);
		}
		set
		{
			if (value == CustomNetTransform.InvalidPos)
			{
				localPos = value;
			}
			else
			{
				localPos = CustomNetTransform.worldToLocal(value);
			}
		}
	}
}

[RequireComponent(typeof(PushPull))]
public class CustomNetTransform : ManagedNetworkBehaviour //see UpdateManager
{
	public static readonly Vector3Int InvalidPos = new Vector3Int(0, 0, -100), deOffset = new Vector3Int(-1, -1, 0);

	private RegisterTile registerTile;

	private TransformState serverTransformState; //used for syncing with players, matters only for server

	public float SpeedMultiplier = 1; //Multiplier for flying/lerping speed, could corelate with weight, for example
	private TransformState transformState; //client's transform, can get dirty/predictive

	private Matrix matrix => registerTile.Matrix;
	[HideInInspector]
	public PushPull pushPull;
	
	public TransformState State => serverTransformState;

	public bool isPushing;

	protected override void OnEnable()
	{
		registerTile = GetComponent<RegisterTile>();
		pushPull = GetComponent<PushPull>();
		base.OnEnable();
	}

	public override void OnStartServer()
	{
		InitServerState();
		base.OnStartServer();
	}

	private void InitServerState()
	{
		if (!isServer)
		{
			return;
		}

		isPushing = false;

		serverTransformState.Speed = 0;
		if (transform.localPosition.Equals(Vector3.zero) || Vector3Int.RoundToInt(transform.position).Equals(InvalidPos) || Vector3Int.RoundToInt(transform.localPosition).Equals(InvalidPos))
		{
			//For stuff hidden on spawn, like player equipment
			serverTransformState.Active = false;
			serverTransformState.localPos = InvalidPos;
		}
		else
		{
			serverTransformState.Active = true;
			serverTransformState.localPos =
				Vector3Int.RoundToInt(new Vector3(transform.localPosition.x, transform.localPosition.y, 0));
		}
	}

	/// Intended for runtime spawning, so that CNT could initialize accordingly
	[Server]
	public void ReInitServerState()
	{
		InitServerState();
		NotifyPlayers();
	}

//	/// Overwrite server state with a completely new one
//    [Server]
//    public void SetState(TransformState state)
//    {
//        serverTransformState = state;
//        NotifyPlayers();
//    }

	/// Manually set an item to a specific position. Use localPosition!
	[Server]
	public void SetPosition(Vector3 pos, bool notify = true, float speed = 4f, bool _isPushing = false)
	{
		if(_isPushing){
			isPushing = true;
			registerTile.ReservePosition(Vector3Int.RoundToInt(pos));
		}
		UpdateServerTransformState(pos, notify, speed);
	}

	/// Apply impulse while setting position
	[Server]
	public void PushTo(Vector3 pos, Vector2 impulseDir, bool notify = true, float speed = 4f, bool _isPushing = false)
	{
		if (IsInSpace()) {
			serverTransformState.Impulse = impulseDir;
		} else {
			SetPosition(pos, notify, speed, _isPushing);
		}
	}

	[Server]
	private void UpdateServerTransformState(Vector3 pos, bool notify = true, float speed = 4f){
		serverTransformState.Speed = speed;
		serverTransformState.localPos = pos;
		//transformState = serverTransformState;
		if (notify) {
			NotifyPlayers();
		}
	}

	//FIXME: deOffset is a temp solution to this weird matrix offset
	public static Vector3 localToWorld(Vector3 localVector3)
	{
		return localVector3 - deOffset;
	}

	public static Vector3 worldToLocal(Vector3 worldVector3)
	{
		return worldVector3 + deOffset;
	}

	/// <summary>
	///     Dropping with some force, in random direction. For space floating demo purposes.
	/// </summary>
	[Server]
	public void ForceDrop(Vector3 pos)
	{
//		GetComponentInChildren<SpriteRenderer>().color = Color.white;
		serverTransformState.Active = true;
		serverTransformState.position = pos;
		Vector2 impulse = Random.insideUnitCircle.normalized;
		//don't apply impulses if item isn't going to float in that direction
		Vector3Int newGoal = RoundWithContext(serverTransformState.localPos + (Vector3) impulse, impulse);
		if (CanDriftTo(newGoal))
		{
//			Debug.LogFormat($"ForceDrop success: from {pos} to {localToWorld(newGoal)}");
			serverTransformState.Impulse = impulse;
			serverTransformState.Speed = Random.Range(0.5f, 3f);
		}
//		else
//		{
//			Debug.LogWarningFormat($"ForceDrop fail: from {pos} to {localToWorld(newGoal)}");			
//		}
		NotifyPlayers();
	}

	[Server]
	public void DisappearFromWorldServer()
	{
		serverTransformState.Active = false;
		serverTransformState.localPos = InvalidPos;
		NotifyPlayers();
	}

	[Server]
	public void AppearAtPositionServer(Vector3 pos)
	{
		serverTransformState.Active = true;
		SetPosition(pos);
	}

	/// <summary>
	///     Method to substitute transform.parent = x stuff.
	///     You shouldn't really use it anymore,
	///     as there are high level methods that should suit your needs better.
	///     Server-only, client is not being notified
	/// </summary>
	[Server]
	public void SetParent(Transform pos)
	{
		transform.parent = pos;
	}

	/// <summary>
	///     Convenience method to make stuff disappear at position.
	///     For CLIENT prediction purposes.
	/// </summary>
	public void DisappearFromWorld()
	{
		transformState.Active = false;
		transformState.position = InvalidPos;
		updateActiveStatus();
	}

	/// <summary>
	///     Convenience method to make stuff appear at position
	///     For CLIENT prediction purposes.
	/// </summary>
	public void AppearAtPosition(Vector3 pos)
	{
		transformState.Active = true;
		transformState.position = pos;
		transform.localPosition = pos + deOffset;
		updateActiveStatus();
	}

	public void UpdateClientState(TransformState newState, bool _isPushing = false)
	{
		if(_isPushing){
			isPushing = true;
			registerTile.ReservePosition(Vector3Int.RoundToInt(newState.localPos));
		}
		//Don't lerp (instantly change pos) if active state was changed
		if (transformState.Active != newState.Active /*|| newState.Speed == 0*/)
		{
			transform.localPosition = newState.localPos;
		}
		transformState = newState;
		updateActiveStatus();
	}

	private void updateActiveStatus()
	{
		if (transformState.Active)
		{
			RegisterObjects();
		}
		else
		{
			registerTile.Unregister();
		}
		//todo: Consider moving VisibleBehaviour functionality to CNT. Currently VB doesn't allow predictive object hiding, for example. 
		Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
		for (int i = 0; i < renderers.Length; i++)
		{
			renderers[i].enabled = transformState.Active;
		}
	}

	/// <summary>
	///     Currently sending to everybody, but should be sent to nearby players only
	/// </summary>
	[Server]
	private void NotifyPlayers()
	{
		TransformStateMessage.SendToAll(gameObject, serverTransformState, isPushing);
	}

	/// <summary>
	///     Sync with new player joining
	/// </summary>
	/// <param name="playerGameObject"></param>
	[Server]
	public void NotifyPlayer(GameObject playerGameObject)
	{
		TransformStateMessage.Send(playerGameObject, gameObject, serverTransformState,
		                           pushPull.isBeingPulled, pushPull.pulledBy);
	}


	//managed by UpdateManager
	public override void UpdateMe()
	{
		if (!registerTile) {
			registerTile = GetComponent<RegisterTile>();
		}
		Synchronize();
	}

	private void RegisterObjects()
	{
		//Register item pos in matrix
		registerTile.UpdatePosition();
	}

	private void Synchronize()
	{
		if (!transformState.Active)
		{
			return;
		}

		if (isServer)
		{
			if (IsInSpace() && !pushPull.isBeingPulled) {
				CheckSpaceDrift();
			}
		} 

		if (IsFloating() && IsInSpace() && !pushPull.isBeingPulled)
		{
			SimulateFloating();
		}

		if (transformState.localPos != transform.localPosition)
		{
			Lerp();
		}

		//Registering
		if (registerTilePos() != Vector3Int.RoundToInt(transformState.localPos) && !isPushing)
		{
//			Debug.LogFormat($"registerTile updating {localToWorld(registerTilePos())}->{localToWorld(Vector3Int.RoundToInt(transform.localPosition))}, " +
//			                $"ts={localToWorld(Vector3Int.RoundToInt(transformState.localPos))}");
			RegisterObjects();
		}
	}

	//Has to be called by playersync so the movement is on the same frame as players movement (or else lots of jerky movement for the pulling obj)
	public void AttemptPull(){
		if (pushPull.pulledBy.transform.hasChanged) {
			pushPull.pulledBy.transform.hasChanged = false;
			Vector3 newPos = pushPull.pulledBy.transform.localPosition - (Vector3)pushPull.pullSync.lastDirection;
			newPos.z = transform.localPosition.z;
			Vector3Int checkPos = Vector3Int.RoundToInt(newPos);
			if (matrix.IsPassableAt(checkPos) || matrix.ContainsAt(checkPos, gameObject) ||
			    matrix.ContainsAt(checkPos, pushPull.pulledBy)) {
				transformState.localPos = newPos;
				float journeyLength = Vector3.Distance(transform.localPosition, pushPull.pulledBy.transform.localPosition);
				transformState.Speed = pushPull.pullSync.playerMove.speed * journeyLength;
				//Lerp is called so we don't wait for the next frame:
				Lerp();
				if (isServer) {
					serverTransformState.localPos = newPos;
				}
			}
		}
	}

	///predictive perpetual flying
	private void SimulateFloating()
	{
		transformState.localPos +=
			(Vector3) transformState.Impulse * (transformState.Speed * SpeedMultiplier) * Time.deltaTime;
	}

	private Vector3Int registerTilePos()
	{
		return registerTile.Position;
	}

	private void Lerp()
	{
		if (transformState.Speed.Equals(0))
		{
			transform.localPosition = transformState.localPos;
			return;
		}
		transform.localPosition =
			Vector3.MoveTowards(transform.localPosition, transformState.localPos, transformState.Speed * SpeedMultiplier * Time.deltaTime);
		
		if (isPushing) {
			//Pushing is done, Register the new pos
			if (Vector3.Distance(transform.localPosition, transformState.localPos) < 0.1f) {
				isPushing = false;
				RegisterObjects();
			}
		}
	}

	/// <summary>
	///     Space drift detection is serverside only
	/// </summary>
	[Server]
	private void CheckSpaceDrift()
	{
		if (IsFloating() && matrix != null)
		{
			Vector3 newGoal = serverTransformState.localPos +
			                                      (Vector3) serverTransformState.Impulse * (serverTransformState.Speed * SpeedMultiplier) * Time.deltaTime;
			Vector3Int intGoal = RoundWithContext(newGoal, serverTransformState.Impulse);
			if (CanDriftTo(intGoal))
			{
				if (registerTile.Position != Vector3Int.RoundToInt(transform.localPosition)){
					registerTile.UpdatePosition();
					RpcForceRegisterUpdate();
				}
				//Spess drifting
				serverTransformState.localPos = newGoal;
			}
			else //Stopping drift
			{
				serverTransformState.Impulse = Vector2.zero; //killing impulse, be aware when implementing throw!
				NotifyPlayers();
				registerTile.UpdatePosition();
				RpcForceRegisterUpdate();
			}
		}
	}

	//For space drift, the server will confirm an update is required and inform the clients
	[ClientRpc]
	private void RpcForceRegisterUpdate(){
		registerTile.UpdatePosition();
	}

	///Special rounding for collision detection
	///returns V3Int of next tile
	private static Vector3Int RoundWithContext(Vector3 roundable, Vector2 impulseContext)
	{
		float x = impulseContext.x;
		float y = impulseContext.y;
		return new Vector3Int(
			x < 0 ? (int) Math.Floor(roundable.x) : (int) Math.Ceiling(roundable.x),
			y < 0 ? (int) Math.Floor(roundable.y) : (int) Math.Ceiling(roundable.y),
			0);
	}

	public bool IsInSpace(){
		return matrix.IsSpaceAt(Vector3Int.RoundToInt(transform.localPosition));
	}

	public bool IsFloating()
	{
		if (isServer)
		{
			return serverTransformState.Impulse != Vector2.zero && serverTransformState.Speed != 0f;
		}
		return transformState.Impulse != Vector2.zero && transformState.Speed != 0f;
	}

	/// Make sure to use localPos when asking
	private bool CanDriftTo(Vector3Int goal)
	{
		return matrix != null && matrix.IsEmptyAt(goal);
	}
}