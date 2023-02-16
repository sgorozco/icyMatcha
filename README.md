# icyMatcha

A multiple-dispatch library for dotNet.

Multiple-dispatch (also known as multi-methods) is a feature of some some programming languages in which a function or method can be dynamically dispatched based on the run-time (dynamic) type of more than one of its arguments. This is a generalization of single-dispatch polymorphism where a function or method call is dynamically dispatched based on the actual derived type of the object on which the method has been called. 
   
This library efficiently routes the dynamic dispatch to the implementing function or method of a class that best matches the types of both the method that has been called, as well as the types of the arguments, taking into account their inheritance hierarchies.
