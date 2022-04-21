open System

open Pholly
open Retry
open CircuitBreaker
open System.Threading.Tasks

let retryPolicyDemo () =
  let random = Random((DateTime.UtcNow.Ticks % (Int32.MaxValue |> int64)) |> int32)
  let workload () =
    let n = random.Next(10)
    if n > 6 then n |> Ok else "Out of range" |> Error
  let asyncWorkload () = task {
    let n = random.Next(10)
    return if n > 6 then n |> Ok else "Out of range" |> Error
  }  
  
  let asyncRetryDemo = task {    
    let retryPolicy =
      Policy.retryAsync [
        retry (upto 10<times>)
        withIntervalsOf [50<ms> ; 150<ms> ; 500<ms>]
        beforeEachRetry (fun _ t _ -> printf $"onRetry %d{t}\n" ; ())
      ]    
    
    printf "This demo uses a random number it is highly likely to succeed but is not guaranteed to\n"
    let! resultOne = asyncWorkload |> retryPolicy
    printf "The next demo will never succeed - it always errors, but shows 10 retries\n"
    let! resultTwo = (fun () -> task { return "Error" |> Error }) |> retryPolicy
        
    return resultOne, resultTwo
  }
  
  printf "\n# Async Retry Policy Demo\n\n"
  
  match asyncRetryDemo |> Async.AwaitTask |> Async.RunSynchronously with
  | Ok r1, Ok r2 -> printf $"%d{r1},%d{r2}\n\n"
  | Ok r1, Error e2 -> printf $"%d{r1},%s{e2}\n"
  | Error e1, Ok r2 -> printf $"%s{e1},%d{r2}\n"
  | _ -> printf "Both went wrong!"
  
  let result =
    workload |> Policy.retryForever [ withIntervalOf 50<ms> ; beforeEachRetry (fun _ t _ -> printf "foreverRetry %d\n" t ; ()) ]
  printf $"We have to succeed as we retry forever %d{result}\n"
  
let circuitBreakerDemo () =
  let breakerExecute,breakerReset,breakerIsolate =
    Policy.circuitBreaker [
      breakOn 3<consecutiveErrors>
      resetAfter (30 |> seconds)
      whenCircuitIsOpened (fun _ _ _ -> printf "Breaker hit\n" ; ())
      whenCircuitIsOpenReturn ("Circuit is open" |> Error)
    ]
    
  printf "\n\n# Circuit Breaker Demo\n\n"
  
  let outputBreakerResult result = match result with | Ok _ -> () | Error e -> printf $"Circuit breaker error: %s{e}\n"
  
  breakerExecute (fun () -> printf "cb1\n" ; "Gone wrong 1" |> Error) |> outputBreakerResult
  breakerExecute (fun () -> printf "cb2\n" ; "Gone wrong 2" |> Error) |> outputBreakerResult
  breakerExecute (fun () -> printf "cb3\n" ; "Gone wrong 3 - should trip breaker" |> Error) |> outputBreakerResult
  breakerExecute (fun () -> printf "cb4\n" ; "Gone wrong 4 - should not be printed" |> Error) |> outputBreakerResult
  breakerReset ()
  breakerExecute (fun () -> printf "cb5\n" ; "Gone wrong 5 - should be printed, breaker reset" |> Error) |> outputBreakerResult
  breakerIsolate ()
  breakerExecute (fun () -> printf "cb6\n" ; "Gone wrong 6 - should not be printed, breaker manually isolated" |> Error) |> outputBreakerResult

  printf "\n\n# Async Circuit Breaker Demo\n\n"
  
  let outputBreakerResultAsync (result:Task<Result<string,string>>) = task {
    match! result with | Ok s -> printf "%s" s | Error e -> printf "Error: %s\n" e
  }

  let executeAsync,_,_ =
      Policy.circuitBreakerAsync [
          breakOn 3<consecutiveErrors>
          whenCircuitIsOpenReturn ("Circuit is open" |> Error)
      ]
      
  let asyncSuccessWorkload () =
    task { return "Success" |> Ok }
  let asyncErrorWorkload () =
    task { return "Error" |> Error }
  task {
      do! asyncSuccessWorkload |> executeAsync |> outputBreakerResultAsync
      do! asyncErrorWorkload |> executeAsync |> outputBreakerResultAsync
      do! asyncErrorWorkload |> executeAsync |> outputBreakerResultAsync
      do! asyncErrorWorkload |> executeAsync |> outputBreakerResultAsync
      do! asyncSuccessWorkload |> executeAsync |> outputBreakerResultAsync    
  } |> Async.AwaitTask |> Async.RunSynchronously
  
let fallbackDemo () =
  printf "\n\n# Fallback Demo\n\n"
  
  let fallbackPolicy =
    Policy.fallbackWith 99
    
  let resultOne = fallbackPolicy (fun _ -> "It went wrong" |> Error)
  printf $"Using the fallback value because this went wrong: %d{resultOne}\n"
  let resultTwo = fallbackPolicy (fun _ -> 42 |> Ok)
  printf $"Should return the meaning of life as this worked: %d{resultTwo}\n"
  
  let asyncFallbackPolicy = Policy.fallbackAsyncWith 101
  let resultThree = (fun () -> task { return "Eeeek" |> Error }) |> asyncFallbackPolicy |> Async.AwaitTask |> Async.RunSynchronously
  printf $"Using the fallback value of 101 because this went wrong: %d{resultThree}\n"
  let resultFour = (fun () -> task { return 84 |> Ok }) |> asyncFallbackPolicy |> Async.AwaitTask |> Async.RunSynchronously
  printf $"Should return the meaning of life * 2 as this worked: %d{resultFour}\n"
  
let policyCompositionDemo () =
  printf "\n\n# Combined Policies Demo\n"
  printf "------------------------\n"
  
  let breaker,_,_ = Policy.circuitBreaker [
    breakOn 3<consecutiveErrors>
    whenCircuitIsOpenReturn ("Circuit is open, execution blocked" |> Error)
    whenCircuitIsOpened (fun _ _ _ -> printf "Circuit opening\n")
  ]
  let retry = Policy.retry [
    retry (upto 10<times>)
    beforeEachRetry (fun r _ _ -> printf "Retrying after error: %s \n" r)
  ]
  let fallback = Policy.fallbackWith 42  
  let failingWorkload = fun () -> "Always going wrong to show error control flow" |> Error
  
  let result = failingWorkload |> breaker -|> retry -|> fallback
  
  printf "Final result %d" result
  
let policyCompositionAsyncDemo () =
  printf "\n\n# Async Policy Composition Demo\n"  
  task {  
    let breaker,_,_ = Policy.circuitBreakerAsync [
      breakOn 3<consecutiveErrors>
      whenCircuitIsOpenReturn ("Circuit is open, execution blocked" |> Error)
      whenCircuitIsOpened (fun _ _ _ -> printf "Circuit opening\n")
    ]
    let retry = Policy.retryAsync [
      retry (upto 10<times>)
      beforeEachRetry (fun r _ _ -> printf "Retrying after error: %s \n" r)
    ]
    let fallback = Policy.fallbackAsyncWith 42  
    let failingAsyncWorkload () = task { return "Always going wrong to show error control flow" |> Error }
    
    let! result = failingAsyncWorkload |> breaker --|> retry --|> fallback
    
    printf "Final result %d" result
    return ()
  } |> Async.AwaitTask |> Async.RunSynchronously
  
  
[<EntryPoint>]
let main _ =
  retryPolicyDemo ()
  circuitBreakerDemo ()
  fallbackDemo ()
  policyCompositionDemo ()
  policyCompositionAsyncDemo ()
  0
