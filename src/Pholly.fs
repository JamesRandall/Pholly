module Pholly

open System
open Polly.CircuitBreaker

(*
Pholly provides a F# style DSL for constructing and using Polly policies. It attempts to:
  1. Be expressive and easy to read
  2. Type safe
  3. Rely on the F# Result<_,_> type for the policy control flow
It takes a fairly typical function builder type approach and the policy constructors return
executers that can be reused so as to avoid repeated reconstruction of policies.
*)

[<Measure>] type ms

let seconds value = value * 1000<ms>
let minutes value = value * (60 |> seconds)
let hours value = value * (60 |> minutes)

module Fallback =
  exception UnsuccessfulFallbackException
  type FallbackConfig<'a,'b> =
    { ShouldFallback: Result<'a,'b> -> bool
      OnFallback: (Polly.DelegateResult<Result<'a,'b>> -> Polly.Context -> unit) option
      OnFallbackAsync: (Polly.DelegateResult<Result<'a,'b>> -> Polly.Context -> Async<unit>) option
    }
    
  let shouldFallback handler config = { config with ShouldFallback = handler }
  
  let whenFallingBack handler config = { config with OnFallback = handler }
  
  let whenFallingBackAsync handler config = { config with OnFallbackAsync = handler }

module CircuitBreaker =
  (* Need to decide if tuples or records / interfaces are a better pattern for results
      let breaker:CircuitBreakerProps.CircuitBreaker<'a,'b> =
        { Execute = execute
          Isolate = fun () -> breakerPolicy.Isolate()
          Reset =  fun () -> breakerPolicy.Reset()
        }
  type CircuitBreaker<'a,'b> =
    { Execute: (unit -> Result<'a,'b>) -> Result<'a,'b>
      Isolate: unit -> unit
      Reset: unit -> unit
    }
  *)
  
  [<Measure>] type consecutiveErrors
  
  type CircuitBreakerConfig<'a,'b> =
    { BreakOn: int<consecutiveErrors>
      ShouldBreak: Result<'a,'b> -> bool
      BreakDuration: int<ms>
      OnBreak: Polly.DelegateResult<Result<'a,'b>> -> TimeSpan -> Polly.Context -> unit
      OnReset: Polly.Context -> unit
      CircuitOpenResult: Result<'a,'b> option
    }
    
  let breakOn consecutiveErrors config = { config with BreakOn = consecutiveErrors }
  let shouldBreak handler config = { config with ShouldBreak = handler }
  let whenCircuitOpened handler config = { config with OnBreak = handler }
  let whenCircuitReset handler config = { config with OnReset = handler }
  let resultWhenCircuitOpen result config = { config with CircuitOpenResult = Some result }
  let resetAfter time config = { config with BreakDuration = time }

module Retry =
  exception RetryForeverFailedException
  [<Measure>] type times
  
  type Retry =
    | Forever
    | Times of int
  
  type RetryConfig<'a,'b> =
    { Retry : Retry
      BackoffSequenceMs : int<ms> list
      BeforeEachRetry : Polly.DelegateResult<Result<'a,'b>>->int->Polly.Context->unit
      ShouldRetry: Result<'a,'b> -> bool
    }    
    
  let retry retries config = { config with Retry = retries }
  let withIntervalOf interval config = { config with BackoffSequenceMs = [interval] }
  let withIntervalsOf backoff config = { config with BackoffSequenceMs = backoff }
  let beforeEachRetry handler config = { config with BeforeEachRetry = handler }
  let shouldRetry handler config = { config with ShouldRetry = handler }
  // syntactic sugar for retries
  let upto (value:int<times>) = value |> int |> Times  
    
module Policy =
  open Polly
  
  let defaultResultComparer = function | Ok _ -> false | Error _ -> true
  
  let fallbackWithOptions<'a,'b> (value:'a) (props:(Fallback.FallbackConfig<'a,'b> -> Fallback.FallbackConfig<'a,'b>) seq) =
    let defaultProps:Fallback.FallbackConfig<'a,'b> =
      { ShouldFallback = defaultResultComparer
        OnFallback = None
        OnFallbackAsync = None
      }
    let config = props |> Seq.fold(fun cfg configFunc -> cfg |> configFunc) defaultProps
    let onFallback = defaultArg config.OnFallback (fun _ _ -> ())
    let fallbackPolicy =
      Policy
        .HandleResult(fun r -> r |> config.ShouldFallback)
        .Fallback(value |> Ok, onFallback)
    let execute workload =
      match fallbackPolicy.Execute(fun () -> workload ()) with
      | Ok value -> value
      | Error _ -> raise Fallback.UnsuccessfulFallbackException
    execute
    
  let fallbackWith value = fallbackWithOptions value []
    
  let fallbackAsyncWithOptions<'a,'b> (value:'a) (props:(Fallback.FallbackConfig<'a,'b> -> Fallback.FallbackConfig<'a,'b>) seq) =
    let defaultProps:Fallback.FallbackConfig<'a,'b> =
      { ShouldFallback = defaultResultComparer
        OnFallback = None
        OnFallbackAsync = None
      }
    let config = props |> Seq.fold(fun cfg configFunc -> cfg |> configFunc) defaultProps
    let onFallback =
      match config.OnFallbackAsync,config.OnFallback with
      | Some onFallbackAsync, _ -> onFallbackAsync
      | _, Some onFallback -> fun dr ctx -> async { onFallback dr ctx }
      | _ -> fun _ _ -> async { return () }
    let fallbackPolicy =
      Policy
        .HandleResult(fun r -> r |> config.ShouldFallback)
        .FallbackAsync(value |> Ok, fun dr ctx -> ((onFallback dr ctx) |> Async.StartAsTask) :> System.Threading.Tasks.Task)
    let execute asyncWorkload = async {
      let! result = fallbackPolicy.ExecuteAsync(fun () -> async { return! asyncWorkload } |> Async.StartAsTask) |> Async.AwaitTask
      return
        match result with
        | Ok value -> value
        | Error _ -> raise Fallback.UnsuccessfulFallbackException
    }
    execute
    
  let fallbackAsyncWith value = fallbackAsyncWithOptions value []
  
  let circuitBreakerAsync<'a,'b> (props:(CircuitBreaker.CircuitBreakerConfig<'a,'b> -> CircuitBreaker.CircuitBreakerConfig<'a,'b>) seq) =
    let defaultProps:CircuitBreaker.CircuitBreakerConfig<'a,'b> =
      { BreakOn = 10<CircuitBreaker.consecutiveErrors>
        ShouldBreak = defaultResultComparer
        BreakDuration = 1 |> minutes
        OnBreak = fun _ _ _ -> ()
        OnReset = fun _ -> ()
        CircuitOpenResult = None
      }
    let config = props |> Seq.fold(fun cfg configFunc -> cfg |> configFunc) defaultProps
    let breakerPolicy = Policy.HandleResult(fun r -> r |> config.ShouldBreak)
    
    let breakerPolicy =
      breakerPolicy.CircuitBreakerAsync(
        config.BreakOn |> int,
        TimeSpan.FromMilliseconds(config.BreakDuration |> double),
        onBreak = config.OnBreak,
        onReset = config.OnReset
      )
    let execute asyncWorkload = async {
      let! choice = breakerPolicy.ExecuteAsync(fun () -> async { return! asyncWorkload } |> Async.StartAsTask)
                    |> Async.AwaitTask
                    |> Async.Catch
      match choice with
      | Choice1Of2 r -> return r
      | Choice2Of2 exn ->
        return match exn with
               | :? AggregateException as exn when (exn.InnerException :? BrokenCircuitException) ->
                 match config.CircuitOpenResult with
                 | Some circuitOpenResult -> circuitOpenResult
                 | None -> raise exn
               // I think I can remove the below but am leaving in until more testing confirms
               | :? BrokenCircuitException as exn ->
                 match config.CircuitOpenResult with
                 | Some circuitOpenResult -> circuitOpenResult
                 | None -> raise exn
               | _ -> raise exn
    }

    (execute, breakerPolicy.Reset, breakerPolicy.Isolate)
  
  let circuitBreaker<'a,'b> (props:(CircuitBreaker.CircuitBreakerConfig<'a,'b> -> CircuitBreaker.CircuitBreakerConfig<'a,'b>) seq) =
    let defaultProps:CircuitBreaker.CircuitBreakerConfig<'a,'b> =
      { BreakOn = 10<CircuitBreaker.consecutiveErrors>
        ShouldBreak = defaultResultComparer
        BreakDuration = 1 |> minutes
        OnBreak = fun _ _ _ -> ()
        OnReset = fun _ -> ()
        CircuitOpenResult = None
      }
    let config = props |> Seq.fold(fun cfg configFunc -> cfg |> configFunc) defaultProps
    let breakerPolicy = Policy.HandleResult(fun r -> r |> config.ShouldBreak)
    
    let breakerPolicy =
      breakerPolicy.CircuitBreaker(
        config.BreakOn |> int,
        TimeSpan.FromMilliseconds(config.BreakDuration |> double),
        onBreak = config.OnBreak,
        onReset = config.OnReset
      )
    let execute workload =
      try
        breakerPolicy.Execute(fun () -> workload ())
      with
      | :? BrokenCircuitException as exn ->
        match config.CircuitOpenResult with
        | Some circuitOpenResult -> circuitOpenResult
        | None -> raise exn
    
    (execute, breakerPolicy.Reset, breakerPolicy.Isolate)
    
  let retryAsync<'a,'b> (retryProps:(Retry.RetryConfig<'a,'b> -> Retry.RetryConfig<'a,'b>) seq) =
    let defaultProps:(Retry.RetryConfig<'a,'b>) =
      { Retry = Retry.Times 10
        BackoffSequenceMs = List.empty
        BeforeEachRetry = fun _ _ _ -> ()
        ShouldRetry = function | Ok _ -> false | Error _ -> true
      }
    let config = retryProps |> Seq.fold (fun cfg configFunc -> cfg |> configFunc) defaultProps
    
    let durationProvider =
      fun (retryAttempt:int) _ -> TimeSpan.FromMilliseconds(config.BackoffSequenceMs.[min retryAttempt (config.BackoffSequenceMs.Length-1)] |> double)
    let retryPolicy = Policy.HandleResult(fun r -> r |> config.ShouldRetry)
    let retryPolicy =
      match config.Retry with
      | Retry.Times times ->
        match config.BackoffSequenceMs |> Seq.isEmpty with
        | true ->
          retryPolicy.RetryAsync(times |> int,onRetry=config.BeforeEachRetry)
        | false ->
          let wrappedHandler = (fun r (_:TimeSpan) -> config.BeforeEachRetry r)
          retryPolicy.WaitAndRetryAsync(times |> int, durationProvider,wrappedHandler)
      | Retry.Forever ->
        match config.BackoffSequenceMs |> Seq.isEmpty with
        | true ->
          retryPolicy.RetryForeverAsync(onRetry=config.BeforeEachRetry)
        | false ->          
          let wrappedHandler = (fun r i (_:TimeSpan) ctx -> config.BeforeEachRetry r i ctx)
          retryPolicy.WaitAndRetryForeverAsync(durationProvider,wrappedHandler)
    let execute asyncWorkload =
      retryPolicy.ExecuteAsync(fun () -> async { return! asyncWorkload } |> Async.StartAsTask) |> Async.AwaitTask
    execute
    
  let retry<'a,'b> (retryProps:(Retry.RetryConfig<'a,'b> -> Retry.RetryConfig<'a,'b>) seq) =
    let defaultProps:(Retry.RetryConfig<'a,'b>) =
      { Retry = Retry.Times 10
        BackoffSequenceMs = List.empty
        BeforeEachRetry = fun _ _ _ -> ()
        ShouldRetry = function | Ok _ -> false | Error _ -> true
      }
    let config =
      retryProps
      |> Seq.fold (fun cfg configFunc -> configFunc cfg) defaultProps
    
    let retryPolicy = Policy.HandleResult(fun r -> r |> config.ShouldRetry)
    let durationProvider =
      fun retryAttempt _ -> TimeSpan.FromMilliseconds(config.BackoffSequenceMs.[min retryAttempt (config.BackoffSequenceMs.Length-1)] |> double)
    let retryPolicy =
      match config.Retry with
      | Retry.Times times ->
        match config.BackoffSequenceMs |> Seq.isEmpty with
        | true ->
          retryPolicy.Retry(times |> int,onRetry=config.BeforeEachRetry)
        | false ->
          let wrappedHandler = (fun r (_:TimeSpan) -> config.BeforeEachRetry r)
          retryPolicy.WaitAndRetry(times |> int, durationProvider,wrappedHandler)
      | Retry.Forever ->
        match config.BackoffSequenceMs |> Seq.isEmpty with
        | true ->
          retryPolicy.RetryForever(onRetry=config.BeforeEachRetry)
        | false ->          
          let wrappedHandler = (fun r i (_:TimeSpan) ctx -> config.BeforeEachRetry r i ctx)
          retryPolicy.WaitAndRetryForever(durationProvider,wrappedHandler)
    let execute workload =
      retryPolicy.Execute(fun () -> workload ())
    execute
    
  // we separate out retry forever as this means we can simply return 'a rather than Result<'a,'b> simplifying
  // usage for the caller
  let retryForever<'a,'b> (retryProps:(Retry.RetryConfig<'a,'b> -> Retry.RetryConfig<'a,'b>) seq) =
    let forever (config:Retry.RetryConfig<'a,'b>) = { config with Retry = Retry.Forever }
    let resultExecute = retry<'a,'b> ([forever] |> Seq.append retryProps) 
    let execute workload =
      let result = workload |> resultExecute
      match result with
      | Ok r -> r
      | Error _ -> raise Retry.RetryForeverFailedException // this should not occur as retrying until ok
    execute
    
  let retryForeverAsync<'a,'b> (retryProps:(Retry.RetryConfig<'a,'b> -> Retry.RetryConfig<'a,'b>) seq) =
    let forever (config:Retry.RetryConfig<'a,'b>) = { config with Retry = Retry.Forever }
    let resultExecute = retryAsync<'a,'b> ([forever] |> Seq.append retryProps)
    let executeAsync asyncWorkload = async {
      let! result = asyncWorkload |> resultExecute
      return
        match result with
        | Ok r -> r
        | Error _ -> raise Retry.RetryForeverFailedException
    }
    executeAsync