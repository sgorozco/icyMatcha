# icyMatcha

A multiple-dispatch library for dotNet.

Multiple-dispatch (also known as multi-methods) is a feature of some some programming languages in which a function or method can be dynamically dispatched based on the run-time (dynamic) type of more than one of its arguments. This is a generalization of single-dispatch polymorphism where a function or method call is dynamically dispatched based on the actual derived type of the object on which the method has been called. 
   
This library efficiently routes the dynamic dispatch to the implementing function or method of a class that best matches the types of both the class implementing the method that has been called, as well as the types of the arguments, taking into account their inheritance hierarchies.

## Strategy

1. The best-matching method is selected via Reflection; it does an outstanding job of selecting the best fitting method according to the involved types.
2. Instead of using a slow ´Invoke()´ call on the method, a delegate is dynamically compiled that will peform a "fast" invocation on the method.
3. The generated delegates are cached and reused:
    - A unique key is computed for the method's signature.
    - The compiled delegates are cached in a concurrent dictionary, keyed by ther signature
    - Before incurring in the slow Reflection method lookup, the dictionary is queried to see if a proper delegate already exists. Reflection is only involved once per method signature.
 4. The method gets invoked via two delegates, a weakly-typed delegate (allowing us to compile for unknown types at compilation time), that forwards the call to the strongly-typed, dynamically-compiled delegate.


## What advantages does the library provide?

The library allows you to transform your dispatching code from looking something similar to this:

```c#
public virtual OperationResult Dispatch(object dto) {
    switch( dto ) {
    case AddCustomerOperation addOperation: 
        return doCustomerAdd(addOperation);
    case UpdateCustomerOperation updateOperation:
        return doCustomerUpdate(updateOperation);
    case DeleteCustomerOperation deleteOperation:
        return doCustomerDelete(deleteOperation);
    ...
    default:
        Console.WriteLine($"unknown DTO type: {object.GetType()}");
        return OperationResult.Success;
   }
}

protected virtual OperationResult doCustomerAdd(AddCustomerOperation addOperation) {
    ...
}

protected virtual OperationResult doCustomerUpdate(UpdateCustomerOperation updateOperation) {
    ...
}

protected virtual OperationResult doCustomerDelete(DeleteCustomerOperation deleteOperation) {
    ...
}

```

To this:

```c#

private DispatchResolver Dispatcher = new FuncDispatchResolver<Func<object, object, OperationResult>>(nameOfMethod: "Handle"); 
...

public OperationResult Dispatch(object dto) {
   var handler = this.Dispatcher.GetFuncDelegate(this.GetType(), dto.GetType());
   return handler(this, dto);
}

// The following methods will be dynamically called by the Dispatcher member
// Marking them as virtual would allow an inherited class to handle the same type of operation DTO differently,
// the multi-dispatch mechanism will select the overriden handler if this is the case.

// Fall-through handler, any unknown type will be routed to this method
protected virtual OperationResult Handle(object _) { 
    Console.WriteLine($"unknown DTO type: {object.GetType()}");
    return OperationResult.Success;
}

protected virtual OperationResult Handle(AddCustomerOperation addOperation) {
   ...
}

protected virtual OperationResult Handle(UpdateCustomerOperation updateOperation) {
   ...
}

protected virtual OperationResult Handle(DeleteCustomerOperation deleteOperation) {
   ...
}
```

As you can see, you no longer need to maintain or edit the Dispatch switch statement when new types of DTO operations need to be handled. You just need to provide a new  method named `Handle` that receives the new type of DTO and returns an `OperationResult` and the library will route your DTO accordingly.

In the previous example, the explicit dispatcher was simple to code, as it is only performing a switch on a single parameter's type, but it would become much more complex if it had to select a handler based on the type of two or more parameters. This is handled nicely by the library.






