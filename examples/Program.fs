open System

open Pholly
open Retry
open CircuitBreaker

let retryPolicyDemo () =
  let random = Random((DateTime.UtcNow.Ticks % (Int32.MaxValue |> int64)) |> int32)
  let workload () =
    let n = random.Next(10)
    if n > 6 then n |> Ok else "Out of range" |> Error
  let asyncWorkload = async {
    let n = random.Next(10)
    return if n > 6 then n |> Ok else "Out of range" |> Error
  }
  
  let asyncRetryDemo = async {
    
    let retryPolicy =
      Policy.retryAsync [
        retry (upto 10<times>)
        withIntervalsOf [50<ms> ; 150<ms> ; 500<ms>]
        beforeEachRetry (fun _ t _ -> printf "onRetry %d\n" t ; ())
      ]    
    
    printf "This demo uses a random number it is highly likely to succeed but is not guaranteed to\n"
    let! resultOne = asyncWorkload |> retryPolicy
    printf "The next demo will never succeed - it always errors, but shows 10 retries\n"
    let! resultTwo = async { return "Error" |> Error } |> retryPolicy
        
    return resultOne, resultTwo
  }
  
  printf "# Retry Policy Demo\n\n"
  
  match asyncRetryDemo |> Async.RunSynchronously with
  | Ok r1, Ok r2 -> printf "%d,%d\n\n" r1 r2
  | Ok r1, Error e2 -> printf "%d,%s\n" r1 e2
  | Error e1, Ok r2 -> printf "%s,%d\n" e1 r2
  | _ -> printf "Both went wrong!"
  
  let result =
    workload |> Policy.retryForever [ withIntervalOf 50<ms> ; beforeEachRetry (fun _ t _ -> printf "foreverRetry %d\n" t ; ()) ]
  printf "We have to succeed as we retry forever %d\n" result
  
let circuitBreakerDemo () =
  let breakerExecute,breakerReset,breakerIsolate =
    Policy.circuitBreaker [
      breakOn 3<consecutiveErrors>
      resetAfter (30 |> seconds)
      whenCircuitIsOpened (fun _ _ _ -> printf "Breaker hit\n" ; ())
      whenCircuitIsOpenReturn ("Circuit is open" |> Error)
    ]
    
  printf "\n\n# Circuit Breaker Demo\n\n"
  
  let outputBreakerResult result = match result with | Ok _ -> () | Error e -> printf "Circuit breaker error: %s\n" e
  
  breakerExecute (fun _ -> printf "cb1\n" ; "Gone wrong 1" |> Error) |> outputBreakerResult
  breakerExecute (fun _ -> printf "cb2\n" ; "Gone wrong 2" |> Error) |> outputBreakerResult
  breakerExecute (fun _ -> printf "cb3\n" ; "Gone wrong 3 - should trip breaker" |> Error) |> outputBreakerResult
  breakerExecute (fun _ -> printf "cb4\n" ; "Gone wrong 4 - should not be printed" |> Error) |> outputBreakerResult
  breakerReset ()
  breakerExecute (fun _ -> printf "cb5\n" ; "Gone wrong 5 - should be printed, breaker reset" |> Error) |> outputBreakerResult
  breakerIsolate ()
  breakerExecute (fun _ -> printf "cb6\n" ; "Gone wrong 6 - should not be printed, breaker manually isolated" |> Error) |> outputBreakerResult

  let outputBreakerResultAsync result = async {
    match! result with | Ok s -> printf "%s" s | Error e -> printf "Error: %s\n" e
  }

  let executeAsync,_,_ =
      Policy.circuitBreakerAsync [
          breakOn 3<consecutiveErrors>
          whenCircuitIsOpenReturn ("Circuit is open" |> Error)
      ]

  async {
      do! async { return "Success" |> Ok } |> executeAsync |> outputBreakerResultAsync
      do! async { return "Error" |> Error } |> executeAsync |> outputBreakerResultAsync
      do! async { return "Error" |> Error } |> executeAsync |> outputBreakerResultAsync
      do! async { return "Error" |> Error } |> executeAsync |> outputBreakerResultAsync
      do! async { return "Success" |> Ok } |> executeAsync |> outputBreakerResultAsync    
  } |> Async.RunSynchronously
  
let fallbackDemo () =
  printf "\n\n# Fallback Demo\n\n"
  
  let fallbackPolicy =
    Policy.fallbackWith 99
    
  let resultOne = fallbackPolicy (fun _ -> "It went wrong" |> Error)
  printf "Using the fallback value because this went wrong: %d\n" resultOne
  let resultTwo = fallbackPolicy (fun _ -> 42 |> Ok)
  printf "Should return the meaning of life as this worked: %d\n" resultTwo
  
  let asyncFallbackPolicy = Policy.fallbackAsyncWith 101
  let resultThree = async { return "Eeeek" |> Error } |> asyncFallbackPolicy |> Async.RunSynchronously
  printf "Using the fallback value of 101 because this went wrong: %d\n" resultThree
  let resultFour = async { return 84 |> Ok } |> asyncFallbackPolicy |> Async.RunSynchronously
  printf "Should return the meaning of life * 2 as this worked: %d\n" resultFour
  
[<EntryPoint>]
let main _ =
  retryPolicyDemo ()
  circuitBreakerDemo ()
  fallbackDemo ()
  0